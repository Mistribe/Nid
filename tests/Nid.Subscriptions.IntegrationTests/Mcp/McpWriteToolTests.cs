using System.Text.Json;
using ModelContextProtocol.Client;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Subscriptions.IntegrationTests.Admin;
using Nid.Subscriptions.IntegrationTests.Proposals;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Mcp;

/// <summary>Spec 4D1 §5–§6: the write tools end to end — conforme, proposed, applied, refused.</summary>
[Collection("postgres")]
public class McpWriteToolTests(PostgresFixture fixture)
{
    private static async Task<McpClient> ClientAsync(NidApp app, string preset)
        => await McpHarness.ClientAsync(app, (await TokenRouteTests.CreateAsync(app, app.AdultA, preset)).Secret);

    private static async Task<(bool Error, string Text)> CallAsync(McpClient client, string tool, object args)
    {
        var result = await client.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!);
        return (result.IsError == true, McpHarness.TextOf(result));
    }

    private static async Task<JsonElement> AnswerAsync(McpClient client, string tool, object args)
    {
        var (error, text) = await CallAsync(client, tool, args);
        Assert.False(error, text);
        return JsonDocument.Parse(text).RootElement;
    }

    private static decimal Amount(JsonElement money) => decimal.Parse(money.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<int> ProposalCountAsync(NidApp app)
        => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, ProposalRouteTests.Proposals)).RootElement.GetProperty("items").GetArrayLength();

    private static object Statement(Guid envelope, bool apply = false) => new
    {
        envelopeId = envelope, asOf = "2025-09-30", value = new { amount = "23500.00", currency = "EUR" },
        movements = new[] { new { on = "2025-09-05", kind = "contribution", amount = "150.00", label = "Versement", @ref = "R-1" } },
        sourceLabel = "Relevé PEA septembre", apply,
    };

    // Review Focus 1.
    [Fact]
    public async Task A_statement_is_proposed_once_then_applied_directly_skipping_what_is_recorded()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);

        await using (var propose = await ClientAsync(app, TokenPresets.Propose))
        {
            var first = await AnswerAsync(propose, "propose_statement", Statement(envelope));
            Assert.Equal(("proposed", true), (first.GetProperty("outcome").GetString(), first.GetProperty("created").GetBoolean()));
            Assert.Equal("investments.record_statement", first.GetProperty("proposal").GetProperty("kind").GetString());
            Assert.False((await AnswerAsync(propose, "propose_statement", Statement(envelope))).GetProperty("created").GetBoolean());

            // D2: refused, never silently turned into a proposal.
            var (error, text) = await CallAsync(propose, "propose_statement", Statement(envelope, apply: true));
            Assert.True(error);
            Assert.Contains("write:direct", text);
        }
        Assert.Equal(1, await ProposalCountAsync(app));

        await using var direct = await ClientAsync(app, TokenPresets.Direct);
        var applied = await AnswerAsync(direct, "propose_statement", Statement(envelope, apply: true));
        Assert.Equal(("applied", 2), (applied.GetProperty("outcome").GetString(), applied.GetProperty("appliedRefs").GetArrayLength()));
        var again = await AnswerAsync(direct, "propose_statement", Statement(envelope, apply: true));
        Assert.Equal(1, again.GetProperty("appliedRefs").GetArrayLength()); // the value again; the movement left out
        Assert.Equal(("payload.movements[0]", ProposalCodes.AlreadyRecorded),
            (again.GetProperty("skipped")[0].GetProperty("field").GetString(), again.GetProperty("skipped")[0].GetProperty("code").GetString()));
        var flows = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/investments/envelopes/{envelope}")).RootElement.GetProperty("flows");
        Assert.Single(flows.EnumerateArray(), f => f.GetProperty("externalRef").GetString() == "R-1");
        Assert.Equal(1, await ProposalCountAsync(app));
    }

    // Review Focus 3.
    [Fact]
    public async Task A_refused_field_is_named_by_its_path_and_nothing_is_proposed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        await using var propose = await ClientAsync(app, TokenPresets.Propose);

        var (error, text) = await CallAsync(propose, "propose_statement", new
        {
            envelopeId = envelope, asOf = "2025-09-30",
            movements = new[]
            {
                new { on = "2025-09-05", kind = "contribution", amount = "150.00" },
                new { on = "2025-09-06", kind = "contribution", amount = "1 234,56" },
            },
        });

        Assert.True(error);
        Assert.StartsWith("Refused: request.invalid.", text);
        Assert.Contains("payload.movements[1].amount:", text);
        Assert.Equal(0, await ProposalCountAsync(app));
    }

    [Fact]
    public async Task A_read_token_cannot_propose_and_another_adults_private_item_is_not_found()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await app.NewEnvelopeAsync("Privé", app.AdultB, Visibility.Private);
        var body = new { envelopeId = hidden.Value, asOf = "2025-09-30", value = new { amount = "1.00", currency = "EUR" } };

        await using (var read = await ClientAsync(app, TokenPresets.Read))
        {
            var (error, text) = await CallAsync(read, "propose_statement", body);
            Assert.True(error);
            Assert.Contains("'write' scope", text);
        }
        await using var propose = await ClientAsync(app, TokenPresets.Propose);
        Assert.Equal((true, "Not found."), await CallAsync(propose, "propose_statement", body));
    }

    /// <summary>Task 2's review (a): what a direct write records is attributed to the token that wrote it.</summary>
    [Fact]
    public async Task A_direct_writes_flows_and_valuation_are_in_the_write_log_with_the_tokens_id()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        var token = await TokenRouteTests.CreateAsync(app, app.AdultA, TokenPresets.Direct);
        await using (var client = await McpHarness.ClientAsync(app, token.Secret))
            Assert.Equal("applied", (await AnswerAsync(client, "propose_statement", Statement(envelope, apply: true))).GetProperty("outcome").GetString());

        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var lines = await platform.AuditLog.Where(r => r.Category == "write" && r.Action == "insert" && r.TokenId == token.Token.Id).ToListAsync();
        Assert.Contains(lines, l => l.Entity!.Contains("flow", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, l => l.Entity!.Contains("valuation", StringComparison.OrdinalIgnoreCase));
        Assert.All(lines, l => Assert.Equal(("mcp", app.AdultA.Value), (l.Via, l.PersonId!.Value)));
    }

    /// <summary>Task 2's review (b): a refusal after a first movement was written leaves no flow and no valuation.</summary>
    [Fact]
    public async Task A_direct_write_refused_after_a_first_movement_leaves_nothing_written()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        await using var direct = await ClientAsync(app, TokenPresets.Direct);

        var (error, text) = await CallAsync(direct, "propose_statement", new
        {
            envelopeId = envelope, asOf = "2025-09-30", value = new { amount = "23500.00", currency = "EUR" },
            movements = new[]
            {
                new { on = "2025-09-05", kind = "contribution", amount = "150.00" },
                new { on = "2025-09-06", kind = "contribution", amount = "1000000000000000.00" }, // beyond the Core's bound
            },
            apply = true,
        });

        Assert.True(error);
        Assert.Contains("payload.movements[1].amount:", text); // the Core's own field
        var detail = await ProposalKindHelpers.EnvelopeAsync(app, envelope);
        Assert.DoesNotContain(detail.GetProperty("flows").EnumerateArray(), f => f.GetProperty("occurredOn").GetString() == "2025-09-05");
        Assert.DoesNotContain(detail.GetProperty("valuations").EnumerateArray(), v => v.GetProperty("amount").GetProperty("amount").GetString() == "23500.0000");
    }

    // Review fix 1, direct write.
    [Fact]
    public async Task A_direct_write_of_two_identical_lines_with_one_recorded_writes_the_other()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        await ProposalKindHelpers.RecordFlowAsync(app, envelope, new { occurredOn = "2025-09-05", kind = "contribution", amount = new { amount = "150.00", currency = "EUR" } });
        await using var direct = await ClientAsync(app, TokenPresets.Direct);
        var line = new { on = "2025-09-05", kind = "contribution", amount = "150.00" };

        var applied = await AnswerAsync(direct, "propose_statement", new { envelopeId = envelope, asOf = "2025-09-30", movements = new[] { line, line }, apply = true });

        Assert.Equal(1, applied.GetProperty("appliedRefs").GetArrayLength());
        Assert.Equal("payload.movements[0]", Assert.Single(applied.GetProperty("skipped").EnumerateArray()).GetProperty("field").GetString());
        var flows = (await ProposalKindHelpers.EnvelopeAsync(app, envelope)).GetProperty("flows").EnumerateArray();
        Assert.Equal(2, flows.Count(f => f.GetProperty("occurredOn").GetString() == "2025-09-05"));
    }

    [Fact]
    public async Task A_payslip_is_conforme_proposed_or_applied()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await McpSeed.IncomeSourceAsync(app, app.AdultA); // « Salaire », 2 300 € net a month from 2025-01-01
        object Payslip(string net, bool apply = false)
            => new { incomeSourceId = source, paidOn = "2025-09-28", net = new { amount = net, currency = "EUR" }, sourceLabel = "Bulletin septembre", apply };

        await using (var propose = await ClientAsync(app, TokenPresets.Propose))
        {
            var same = await AnswerAsync(propose, "propose_payslip", Payslip("2290.00"));
            Assert.Equal("conforme", same.GetProperty("outcome").GetString());
            Assert.Equal(2300m, Amount(same.GetProperty("expected").GetProperty("net")));
            Assert.Equal(2290m, Amount(same.GetProperty("given").GetProperty("net")));
            Assert.Equal(0, await ProposalCountAsync(app));

            var raise = await AnswerAsync(propose, "propose_payslip", Payslip("2400.00"));
            Assert.Equal("proposed", raise.GetProperty("outcome").GetString());
            Assert.Equal("2025-09-01", raise.GetProperty("proposal").GetProperty("payload").GetProperty("effectiveOn").GetString());
        }

        await using var direct = await ClientAsync(app, TokenPresets.Direct);
        Assert.Equal("applied", (await AnswerAsync(direct, "propose_payslip", Payslip("2450.00", apply: true))).GetProperty("outcome").GetString());
        var phases = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/income/sources/{source}")).RootElement.GetProperty("phases");
        Assert.Contains(phases.EnumerateArray(), p => p.GetProperty("startsOn").GetString() == "2025-09-01" && Amount(p.GetProperty("netAmount")) == 2450m);
    }

    [Fact]
    public async Task A_payslip_written_directly_is_refused_without_write_direct_and_a_propose_token_cannot_apply_a_proposal()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await McpSeed.IncomeSourceAsync(app, app.AdultA);
        var body = new { incomeSourceId = source, paidOn = "2025-09-28", net = new { amount = "2400.00", currency = "EUR" }, apply = true };

        await using var propose = await ClientAsync(app, TokenPresets.Propose);
        var (error, text) = await CallAsync(propose, "propose_payslip", body);
        Assert.True(error);
        Assert.Contains("write:direct", text);
        Assert.Equal(0, await ProposalCountAsync(app));

        // A token with the propose scope cannot approve either: the REST route needs `apply`.
        var proposed = await AnswerAsync(propose, "propose_payslip", new { incomeSourceId = source, paidOn = "2025-09-28", net = new { amount = "2400.00", currency = "EUR" } });
        var proposal = proposed.GetProperty("proposal");
        var token = await TokenRouteTests.CreateAsync(app, app.AdultA, TokenPresets.Propose);
        var response = await app.BearerAsync(token.Secret, HttpMethod.Post, $"{ProposalRouteTests.Proposals}/{proposal.GetProperty("id").GetGuid()}/approve",
            new { version = proposal.GetProperty("version").GetUInt32() });
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_invoice_is_conforme_proposed_or_applied()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = (await app.NewMonthlyAsync(app.AdultA, 9.99m)).Value;
        object Invoice(string amount, bool apply = false)
            => new { subscriptionId = subscription, chargedOn = "2025-09-01", amount = new { amount, currency = "EUR" }, apply };
        async Task<JsonElement> PhasesAsync()
            => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/subscriptions/{subscription}")).RootElement.GetProperty("phases");

        await using (var propose = await ClientAsync(app, TokenPresets.Propose))
        {
            var same = await AnswerAsync(propose, "propose_invoice", Invoice("9.99"));
            Assert.Equal("conforme", same.GetProperty("outcome").GetString());
            Assert.Equal(9.99m, Amount(same.GetProperty("expected").GetProperty("price")));
            Assert.Equal(0, await ProposalCountAsync(app));
            Assert.Equal(1, (await PhasesAsync()).GetArrayLength());
            Assert.Equal("proposed", (await AnswerAsync(propose, "propose_invoice", Invoice("10.99"))).GetProperty("outcome").GetString());
        }

        await using var direct = await ClientAsync(app, TokenPresets.Direct);
        Assert.Equal("applied", (await AnswerAsync(direct, "propose_invoice", Invoice("10.49", apply: true))).GetProperty("outcome").GetString());
        Assert.Contains((await PhasesAsync()).EnumerateArray(), p => p.GetProperty("startsOn").GetString() == "2025-09-01" && Amount(p.GetProperty("price")) == 10.49m);
        Assert.Equal("conforme", (await AnswerAsync(direct, "propose_invoice", Invoice("10.49", apply: true))).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task An_invoice_written_directly_is_refused_without_write_direct_and_a_propose_token_cannot_apply_a_proposal()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = (await app.NewMonthlyAsync(app.AdultA, 9.99m)).Value;
        var amount = new { amount = "10.99", currency = "EUR" };

        await using var propose = await ClientAsync(app, TokenPresets.Propose);
        var (error, text) = await CallAsync(propose, "propose_invoice", new { subscriptionId = subscription, chargedOn = "2025-09-01", amount, apply = true });
        Assert.True(error);
        Assert.Contains("write:direct", text);
        Assert.Equal(0, await ProposalCountAsync(app));

        var proposal = (await AnswerAsync(propose, "propose_invoice", new { subscriptionId = subscription, chargedOn = "2025-09-01", amount })).GetProperty("proposal");
        var token = await TokenRouteTests.CreateAsync(app, app.AdultA, TokenPresets.Propose);
        var response = await app.BearerAsync(token.Secret, HttpMethod.Post, $"{ProposalRouteTests.Proposals}/{proposal.GetProperty("id").GetGuid()}/approve",
            new { version = proposal.GetProperty("version").GetUInt32() });
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }
}
