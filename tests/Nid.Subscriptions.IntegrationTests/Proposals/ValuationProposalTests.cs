using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Subscriptions.IntegrationTests.Admin;
using Nid.Subscriptions.IntegrationTests.Mcp;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Proposals;

/// <summary>Spec 4C1 §6: an envelope's value, proposed by a token and approved in the browser.</summary>
public class ValuationProposalTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Kind = "investments.record_valuation";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static object Value(Guid envelope, string asOf = "2025-09-30", string amount = "23500.00", string currency = "EUR")
        => new { kind = Kind, targetItemId = envelope, payload = new { asOf, amount = new { amount, currency } }, sourceLabel = "Relevé PEA septembre" };

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Json);

    /// <summary>The envelope's current values, as its detail lists them (amounts as stored: four decimals).</summary>
    private static async Task<List<(Guid Id, string Amount)>> ValuesAsync(NidApp app, Guid id)
        => [.. JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/investments/envelopes/{id}")).RootElement.GetProperty("valuations")
            .EnumerateArray().Select(v => (v.GetProperty("id").GetGuid(), v.GetProperty("amount").GetProperty("amount").GetString()!))];

    [Fact]
    public async Task A_proposed_value_is_previewed_then_recorded_on_approval()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        var propose = await TokenRouteTests.CreateAsync(app, app.AdultA, TokenPresets.Propose);

        Assert.Equal(HttpStatusCode.Forbidden, (await app.BearerAsync(propose.Secret, HttpMethod.Post,
            $"/api/v1/investments/envelopes/{envelope}/valuations", new { asOf = "2025-09-30", amount = new { amount = "23500.00", currency = "EUR" } })).StatusCode);

        var submitted = await app.BearerAsync(propose.Secret, HttpMethod.Post, ProposalRouteTests.Proposals, Value(envelope));
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
        var proposal = await JsonOf(submitted);
        Assert.Equal("2025-09-30", proposal.GetProperty("subjectKey").GetString());
        var id = proposal.GetProperty("id").GetGuid();

        var detail = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{ProposalRouteTests.Proposals}/{id}"));
        var preview = detail.GetProperty("preview");
        Assert.Equal("2025-01-31", preview.GetProperty("previous").GetProperty("asOf").GetString());
        Assert.Equal("21000.0000", preview.GetProperty("previous").GetProperty("amount").GetProperty("amount").GetString());
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("current").ValueKind);
        Assert.Equal("23500.00", preview.GetProperty("proposed").GetProperty("amount").GetProperty("amount").GetString());

        var approved = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{ProposalRouteTests.Proposals}/{id}/approve",
            new { version = detail.GetProperty("proposal").GetProperty("version").GetUInt32() }));
        Assert.Equal("approved", approved.GetProperty("status").GetString());
        var valuation = approved.GetProperty("appliedRefs")[0].GetGuid();
        Assert.Contains((valuation, "23500.0000"), await ValuesAsync(app, envelope));
    }

    [Fact]
    public async Task A_value_already_recorded_on_that_date_is_previewed_as_current()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{envelope}/valuations",
            new { asOf = "2025-09-30", amount = new { amount = "22000.00", currency = "EUR" } })).EnsureSuccessStatusCode();
        var proposal = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, ProposalRouteTests.Proposals, Value(envelope)));

        var preview = (await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get,
            $"{ProposalRouteTests.Proposals}/{proposal.GetProperty("id").GetGuid()}"))).GetProperty("preview");

        Assert.Equal("22000.0000", preview.GetProperty("current").GetProperty("amount").GetProperty("amount").GetString());
        Assert.Equal("2025-01-31", preview.GetProperty("previous").GetProperty("asOf").GetString());
    }

    [Fact]
    public async Task An_edited_payload_is_what_is_applied_and_both_are_kept()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        var proposal = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, ProposalRouteTests.Proposals, Value(envelope)));

        var approved = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{ProposalRouteTests.Proposals}/{proposal.GetProperty("id").GetGuid()}/approve", new
        {
            version = proposal.GetProperty("version").GetUInt32(),
            editedPayload = new { asOf = "2025-09-30", amount = new { amount = "23450.00", currency = "EUR" } },
        }));

        Assert.Equal("23500.00", approved.GetProperty("payload").GetProperty("amount").GetProperty("amount").GetString());
        Assert.Equal("23450.00", approved.GetProperty("editedPayload").GetProperty("amount").GetProperty("amount").GetString());
        var values = await ValuesAsync(app, envelope);
        Assert.Contains(values, v => v.Amount == "23450.0000");
        Assert.DoesNotContain(values, v => v.Amount == "23500.0000");
    }

    // Review Focus 3.
    [Fact]
    public async Task An_invalid_edit_is_refused_and_the_proposal_stays_pending()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        var proposal = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, ProposalRouteTests.Proposals, Value(envelope)));
        var id = proposal.GetProperty("id").GetGuid();

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{ProposalRouteTests.Proposals}/{id}/approve", new
        {
            version = proposal.GetProperty("version").GetUInt32(),
            editedPayload = new { asOf = "2025-09-30", amount = new { amount = "23450.00", currency = "USD" } },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var after = (await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{ProposalRouteTests.Proposals}/{id}"))).GetProperty("proposal");
        Assert.Equal("pending", after.GetProperty("status").GetString());
        Assert.Contains(after.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("code").GetString() == ProposalCodes.ApplyFailed);
        Assert.DoesNotContain(await ValuesAsync(app, envelope), v => v.Amount == "23450.0000");
    }

    [Fact]
    public async Task Submissions_the_envelope_refuses_answer_at_their_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);

        foreach (var (body, path) in new (object Body, string Path)[]
                 {
                     (Value(envelope, currency: "USD"), "payload.amount.currency"),
                     (Value(envelope, currency: "euros"), "payload.amount.currency"),
                     (Value(envelope, asOf: "2026-01-01"), "payload.asOf"),
                     (Value(envelope, asOf: "30/09/2025"), "payload.asOf"),
                     (Value(envelope, amount: "abc"), "payload.amount.amount"),
                 })
        {
            var response = await app.RawAsync(app.AdultA, HttpMethod.Post, ProposalRouteTests.Proposals, body);
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{path}: {response.StatusCode}");
            Assert.Equal(path, (await JsonOf(response)).GetProperty("errors")[0].GetProperty("path").GetString());
        }
    }

    [Fact]
    public async Task A_closed_envelope_makes_the_approval_fail_and_the_proposal_stay_pending()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        var proposal = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, ProposalRouteTests.Proposals, Value(envelope)));
        var id = proposal.GetProperty("id").GetGuid();
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{envelope}/close",
            new { closedOn = "2025-10-01", finalWithdrawal = new { amount = "21000.00", currency = "EUR" } })).EnsureSuccessStatusCode();

        var approved = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{ProposalRouteTests.Proposals}/{id}/approve",
            new { version = proposal.GetProperty("version").GetUInt32() });

        Assert.Equal(HttpStatusCode.Conflict, approved.StatusCode); // investments.envelope_closed answers 409
        Assert.Equal("pending", (await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{ProposalRouteTests.Proposals}/{id}")))
            .GetProperty("proposal").GetProperty("status").GetString());
        Assert.DoesNotContain(await ValuesAsync(app, envelope), v => v.Amount == "23500.0000");
    }

    // Review Focus 1.
    [Fact]
    public async Task Two_concurrent_approvals_apply_once()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);
        var proposal = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, ProposalRouteTests.Proposals, Value(envelope)));
        var url = $"{ProposalRouteTests.Proposals}/{proposal.GetProperty("id").GetGuid()}/approve";
        var body = new { version = proposal.GetProperty("version").GetUInt32() };

        var answers = await Task.WhenAll(app.RawAsync(app.AdultA, HttpMethod.Post, url, body), app.RawAsync(app.AdultA, HttpMethod.Post, url, body));

        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        Assert.Single(answers, a => a.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity);
        Assert.Single(await ValuesAsync(app, envelope), v => v.Amount == "23500.0000");
        var after = (await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{ProposalRouteTests.Proposals}/{proposal.GetProperty("id").GetGuid()}"))).GetProperty("proposal");
        Assert.Equal("approved", after.GetProperty("status").GetString());
        Assert.DoesNotContain(after.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("code").GetString() == ProposalCodes.ApplyFailed);
    }

    /// <summary>Raw bodies (`{target}` stands for the envelope): what a client can send that no record serializes.</summary>
    [Theory]
    [InlineData("""{"targetItemId":"{target}","payload":{"asOf":"2025-09-30","amount":{"amount":"1.00","currency":"EUR"}}}""")]
    [InlineData("""{"kind":"","targetItemId":"{target}","payload":{"asOf":"2025-09-30","amount":{"amount":"1.00","currency":"EUR"}}}""")]
    [InlineData("""{"kind":"investments.record_valuation","targetItemId":"{target}"}""")]
    [InlineData("""{"kind":"investments.record_valuation","targetItemId":"{target}","payload":[]}""")]
    [InlineData("""{"kind":"investments.record_valuation","targetItemId":"{target}","payload":"x"}""")]
    [InlineData("""{"kind":"investments.record_valuation","targetItemId":"{target}","payload":null}""")]
    [InlineData("""{"kind":"investments.record_valuation","targetItemId":"{target}","payload":{"asOf":"2025-09-30","amount":{"amount":"1.00","currency":"EUR"},"x":1e400}}""")]
    public async Task Hostile_submissions_are_refused_or_cleaned_never_a_500(string raw)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, ProposalRouteTests.Proposals,
            JsonDocument.Parse(raw.Replace("{target}", envelope.ToString())).RootElement);

        Assert.True((int)response.StatusCode < 500, $"{raw}: {response.StatusCode}");
        if (response.IsSuccessStatusCode)
            Assert.False((await JsonOf(response)).GetProperty("payload").TryGetProperty("x", out _));
    }

    [Fact]
    public async Task An_unknown_field_is_not_stored_and_does_not_defeat_deduplication()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await McpSeed.EnvelopeAsync(app, app.AdultA);

        var first = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, ProposalRouteTests.Proposals, new
        {
            kind = Kind, targetItemId = envelope, payload = new { asOf = "2025-09-30", amount = new { amount = "23500.00", currency = "EUR" }, junk = "à ignorer" },
        }));
        var again = await app.RawAsync(app.AdultA, HttpMethod.Post, ProposalRouteTests.Proposals, new
        {
            kind = Kind, targetItemId = envelope, payload = new { asOf = "2025-09-30", amount = new { amount = "23500.00", currency = "EUR" }, other = 1 },
        });

        var stored = (await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{ProposalRouteTests.Proposals}/{first.GetProperty("id").GetGuid()}")))
            .GetProperty("proposal").GetProperty("payload");
        Assert.False(stored.TryGetProperty("junk", out _));
        Assert.Equal("23500.00", stored.GetProperty("amount").GetProperty("amount").GetString());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(first.GetProperty("id").GetGuid(), (await JsonOf(again)).GetProperty("id").GetGuid());
    }
}
