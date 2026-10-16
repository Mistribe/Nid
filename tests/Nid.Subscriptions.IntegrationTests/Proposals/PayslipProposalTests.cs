using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.Proposals.ProposalKindHelpers;

namespace Nid.Subscriptions.IntegrationTests.Proposals;

/// <summary>Spec 4D1 §3: a payslip corrects the expected pay; within the tolerance it is « conforme ». Today is 2025-10-15.</summary>
[Collection("postgres")]
public class PayslipProposalTests(PostgresFixture fixture)
{
    private const string Kind = "income.change_pay";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static object Phase(string label = "Salaire", string startsOn = "2025-01-01", string net = "2300", string? gross = null, int months = 1)
        => new Dictionary<string, object?>
        {
            ["label"] = label, ["startsOn"] = startsOn, ["endsOn"] = null, ["netAmount"] = new { amount = net, currency = "EUR" },
            ["grossAmount"] = gross is null ? null : new { amount = gross, currency = "EUR" }, ["every"] = new { count = months, unit = "month" },
        };

    private static async Task<Guid> SourceAsync(NidApp app, params object[] phases)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/income/sources", new Dictionary<string, object?>
        {
            ["displayName"] = "Salaire", ["kind"] = "salary", ["currency"] = "EUR", ["visibility"] = "shared",
            ["owners"] = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } }, ["phases"] = phases,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonOf(response)).GetProperty("id").GetGuid();
    }

    private static object Payslip(Guid source, string net, string paidOn = "2025-09-28", string? gross = null, string? phaseLabel = null)
        => new
        {
            kind = Kind, targetItemId = source, sourceLabel = "Bulletin septembre",
            payload = new Dictionary<string, object?>
            {
                ["paidOn"] = paidOn, ["net"] = new { amount = net, currency = "EUR" },
                ["gross"] = gross is null ? null : new { amount = gross, currency = "EUR" }, ["phaseLabel"] = phaseLabel,
            },
        };

    private static Task<HttpResponseMessage> PostAsync(NidApp app, object body) => app.RawAsync(app.AdultA, HttpMethod.Post, ProposalRouteTests.Proposals, body);

    private static Task<HttpResponseMessage> ApproveEditedAsync(NidApp app, JsonElement proposal, object edited)
        => ApproveAsync(app, proposal, JsonSerializer.SerializeToNode(edited));

    private static async Task<List<JsonElement>> PhasesAsync(NidApp app, Guid source)
        => [.. JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/income/sources/{source}")).RootElement.GetProperty("phases").EnumerateArray()];

    private static decimal Amount(JsonElement money) => decimal.Parse(money.GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<string?> FirstPathAsync(HttpResponseMessage response)
        => (await JsonOf(response)).GetProperty("errors")[0].GetProperty("path").GetString();

    [Fact]
    public async Task Within_the_tolerance_a_payslip_is_conforme_and_creates_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await SourceAsync(app, Phase());

        foreach (var net in new[] { "2300.00", "2254.00", "2346.00" })
            Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, Payslip(source, net))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(app, Payslip(source, "2346.01"))).StatusCode);
    }

    [Fact]
    public async Task The_tolerance_is_a_setting()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string> { ["Income:PayslipTolerance"] = "0" });
        var source = await SourceAsync(app, Phase());

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, Payslip(source, "2300.00"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(app, Payslip(source, "2300.01"))).StatusCode);
    }

    [Fact]
    public async Task Beyond_the_tolerance_the_new_pay_is_proposed_from_the_month_s_payment_date_and_approved()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await SourceAsync(app, Phase());

        var proposal = await SubmitAsync(app, Payslip(source, "2400.00"));
        Assert.Equal("Salaire:2025-09", proposal.GetProperty("subjectKey").GetString());
        Assert.Equal(("Salaire", "2025-09-01"), (proposal.GetProperty("payload").GetProperty("phaseLabel").GetString(),
            proposal.GetProperty("payload").GetProperty("effectiveOn").GetString()));

        var preview = (await ReadAsync(app, proposal)).GetProperty("preview");
        Assert.Equal(2300m, Amount(preview.GetProperty("expected").GetProperty("net")));
        Assert.Equal(2400m, Amount(preview.GetProperty("proposed").GetProperty("net")));
        Assert.Equal("2025-09-01", preview.GetProperty("effectiveOn").GetString());

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, proposal)).StatusCode);
        var phases = await PhasesAsync(app, source);
        Assert.Equal(("2025-09-01", 2300m), (phases[0].GetProperty("endsOn").GetString(), Amount(phases[0].GetProperty("netAmount"))));
        Assert.Equal(("2025-09-01", 2400m), (phases[1].GetProperty("startsOn").GetString(), Amount(phases[1].GetProperty("netAmount"))));
    }

    [Fact]
    public async Task A_gross_that_differs_is_proposed_even_with_the_expected_net()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await SourceAsync(app, Phase(gross: "3000"));

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(app, Payslip(source, "2300.00", gross: "3000.00"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(app, Payslip(source, "2300.00", gross: "3000.01"))).StatusCode);
    }

    [Fact]
    public async Task Two_running_phases_need_a_label()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await SourceAsync(app, Phase(), Phase(label: "Prime", net: "100"));

        var refused = await PostAsync(app, Payslip(source, "2400.00"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("payload.phaseLabel", await FirstPathAsync(refused));
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(app, Payslip(source, "150.00", phaseLabel: "Prime"))).StatusCode);
    }

    // Review Focus 4.
    [Fact]
    public async Task A_month_without_a_payment_and_the_phase_s_first_month_are_refused_at_paidOn()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var quarterly = await SourceAsync(app, Phase(startsOn: "2025-01-15", months: 3)); // pays in January, April, July, October
        var fresh = await SourceAsync(app, Phase(startsOn: "2025-09-01"));

        foreach (var source in new[] { quarterly, fresh })
        {
            var refused = await PostAsync(app, Payslip(source, "2400.00"));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("payload.paidOn", await FirstPathAsync(refused));
        }
    }

    [Fact]
    public async Task An_edit_is_what_is_applied()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await SourceAsync(app, Phase(gross: "3000"));
        var proposal = await SubmitAsync(app, Payslip(source, "2400.00", gross: "3100.00"));

        var approved = await ApproveEditedAsync(app, proposal, new
        {
            paidOn = "2025-09-28", phaseLabel = "Salaire", net = new { amount = "2410.00", currency = "EUR" },
            gross = new { amount = "3110.00", currency = "EUR" }, effectiveOn = "2025-10-01",
        });

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var phases = await PhasesAsync(app, source);
        Assert.Equal(("2025-10-01", 2410m, 3110m), (phases[1].GetProperty("startsOn").GetString(),
            Amount(phases[1].GetProperty("netAmount")), Amount(phases[1].GetProperty("grossAmount"))));
    }

    [Fact]
    public async Task An_edited_date_that_is_not_a_payment_date_is_refused_at_its_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await SourceAsync(app, Phase());
        var proposal = await SubmitAsync(app, Payslip(source, "2400.00"));

        var refused = await ApproveEditedAsync(app, proposal, new { paidOn = "2025-09-28", phaseLabel = "Salaire", net = new { amount = "2400.00", currency = "EUR" }, effectiveOn = "2025-09-15" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("payload.effectiveOn", await FirstPathAsync(refused));
    }

    // Review Focus 5.
    [Fact]
    public async Task An_ended_source_makes_the_approval_fail_and_nothing_change()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await SourceAsync(app, Phase());
        var proposal = await SubmitAsync(app, Payslip(source, "2400.00"));
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/income/sources/{source}/close", new { endedOn = "2025-10-01" })).EnsureSuccessStatusCode();
        var before = await app.GetRawAsync(app.AdultA, $"/api/v1/income/sources/{source}");

        var approved = await ApproveAsync(app, proposal);

        Assert.Equal(HttpStatusCode.Conflict, approved.StatusCode); // income.source_ended answers 409
        Assert.Equal(before, await app.GetRawAsync(app.AdultA, $"/api/v1/income/sources/{source}"));
        var after = (await ReadAsync(app, proposal)).GetProperty("proposal");
        Assert.Equal("pending", after.GetProperty("status").GetString());
        Assert.Contains(after.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("code").GetString() == "apply_failed");
    }

    // Controller ruling P4: absent = not printed, not zero.
    [Fact]
    public async Task A_payslip_without_a_gross_keeps_the_phase_s_gross()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await SourceAsync(app, Phase(gross: "3000"));
        var proposal = await SubmitAsync(app, Payslip(source, "2400.00"));

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(app, proposal)).StatusCode);
        var phases = await PhasesAsync(app, source);
        Assert.Equal((2400m, 3000m), (Amount(phases[1].GetProperty("netAmount")), Amount(phases[1].GetProperty("grossAmount"))));
    }

    [Fact]
    public async Task Another_payslip_of_the_same_phase_and_month_supersedes_the_pending_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await SourceAsync(app, Phase());

        var first = await SubmitAsync(app, Payslip(source, "2400.00"));
        var second = await SubmitAsync(app, Payslip(source, "2500.00", paidOn: "2025-09-29"));

        var old = (await ReadAsync(app, first)).GetProperty("proposal");
        Assert.Equal("superseded", old.GetProperty("status").GetString());
        Assert.Equal(second.GetProperty("id").GetGuid(), old.GetProperty("supersededById").GetGuid());
    }

    // Review Focus 5, narrowed: a validation-time refusal.
    [Fact]
    public async Task A_payslip_for_an_ended_source_is_refused_at_submission()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await SourceAsync(app, Phase());
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/income/sources/{source}/close", new { endedOn = "2025-10-01" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(app, Payslip(source, "2400.00"))).StatusCode);
    }
}
