using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using Duration = NodaTime.Duration;

namespace Nid.Investments.IntegrationTests;

/// <summary>#23: an envelope created by mistake is deleted after a preview, with its holdings, their history and its facts.</summary>
public class EnvelopeDeleteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Envelopes = "/api/v1/investments/envelopes";

    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<JsonElement> SendAsync(NidApp app, HttpMethod method, string url, object? body, HttpStatusCode expected, PersonId? actor = null)
    {
        var response = await app.RawAsync(actor ?? app.AdultA, method, url, body);
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return response.Content.Headers.ContentLength == 0 ? default : await JsonAsync(response);
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    private static async Task<JsonElement> PreviewAsync(NidApp app, PersonId actor, ItemId envelope)
    {
        var response = await app.RawAsync(actor, HttpMethod.Get, $"{Envelopes}/{envelope.Value}/deletion");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static int Removed(JsonElement preview, string key)
        => preview.GetProperty("removes").EnumerateArray().Single(r => r.GetProperty("key").GetString() == key).GetProperty("count").GetInt32();

    private static JsonElement Blocker(JsonElement preview, string key)
        => preview.GetProperty("blockers").EnumerateArray().Single(b => b.GetProperty("key").GetString() == key);

    private static async Task<uint> VersionAsync(NidApp app, ItemId envelope) => (await PreviewAsync(app, app.AdultA, envelope)).GetProperty("version").GetUInt32();

    private static Task<HttpResponseMessage> DeleteAsync(NidApp app, ItemId envelope, uint version)
        => app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Envelopes}/{envelope.Value}?version={version}");

    private static async Task<HttpStatusCode> EnvelopeStatusAsync(NidApp app, ItemId envelope)
        => (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Envelopes}/{envelope.Value}")).StatusCode;

    /// <summary>A holding with a movement, a cost report and a snapshot line, and the envelope's contribution.</summary>
    private static async Task<(ItemId Envelope, Guid Holding)> FilledAsync(NidApp app)
    {
        var id = await app.NewEnvelopeAsync("Assurance-vie", app.AdultA, Visibility.Shared);
        var url = $"{Envelopes}/{id.Value}";
        await SendAsync(app, HttpMethod.Post, $"{url}/flows", new { occurredOn = "2025-01-10", kind = "contribution", amount = Eur("1000.00") }, HttpStatusCode.Created);
        var holding = (await SendAsync(app, HttpMethod.Post, $"{url}/holdings", new { name = "Fonds A", kind = "fund" }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await SendAsync(app, HttpMethod.Post, $"{url}/holdings/{holding}/movements",
            new { occurredOn = "2025-01-10", kind = "allocation_in", amount = Eur("1000.00"), units = "10", unitPrice = "100" }, HttpStatusCode.Created);
        await SendAsync(app, HttpMethod.Put, $"{url}/holdings/{holding}/cost-reports/2024",
            new { grossReturnPct = 8.00m, totalFeesPct = 2.50m, netReturnPct = 5.50m }, HttpStatusCode.OK);
        await SendAsync(app, HttpMethod.Post, $"{url}/holding-snapshots", new
        {
            asOf = "2025-06-30", recordValuation = true,
            lines = new object[] { new { holdingId = holding, units = "10", unitPrice = "110", value = Eur("1100.00") } },
        }, HttpStatusCode.Created);
        return (id, holding);
    }

    private static async Task<Guid> TransferAsync(NidApp app, ItemId from, ItemId to)
        => (await SendAsync(app, HttpMethod.Post, "/api/v1/investments/transfers",
            new { from = from.Value, to = to.Value, occurredOn = "2025-06-15", amount = Eur("200.00") }, HttpStatusCode.Created)).GetProperty("id").GetGuid();

    [Fact]
    public async Task Preview_counts_holdings_and_flows()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (envelope, _) = await FilledAsync(app);

        var preview = await PreviewAsync(app, app.AdultA, envelope);

        Assert.Equal("Assurance-vie", preview.GetProperty("name").GetString());
        Assert.Equal(1, Removed(preview, "holdings"));
        Assert.Equal(1, Removed(preview, "holding_snapshots"));
        Assert.Equal(1, Removed(preview, "flows"));
        Assert.Empty(preview.GetProperty("blockers").EnumerateArray());
    }

    [Fact]
    public async Task Delete_removes_the_envelope_holdings_with_their_history_and_facts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (envelope, holding) = await FilledAsync(app);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(app, envelope, await VersionAsync(app, envelope))).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, await EnvelopeStatusAsync(app, envelope));
        var id = envelope.Value;
        await using var investments = PostgresFixture.NewInvestmentsContext(app.ConnectionString);
        Assert.Empty(investments.Envelopes.Where(e => e.ItemId == id));
        Assert.Empty(investments.Holdings.Where(h => h.EnvelopeItemId == id));
        Assert.Empty(investments.HoldingSnapshots.Where(s => s.EnvelopeItemId == id));
        Assert.Empty(investments.HoldingSnapshotLines.Where(l => l.HoldingId == holding));
        Assert.Empty(investments.HoldingMovements.Where(m => m.HoldingId == holding));
        Assert.Empty(investments.HoldingCostReports.Where(r => r.HoldingId == holding));
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Empty(core.Flows.Where(f => f.ItemId == id));
        Assert.Empty(core.Valuations.Where(v => v.ItemId == id));
    }

    [Fact]
    public async Task Open_goal_funded_by_it_blocks()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA, funding: [(envelope, 5000)]);

        var blocker = Blocker(await PreviewAsync(app, app.AdultA, envelope), "goal_funding");
        await AssertRefusedAsync(await DeleteAsync(app, envelope, await VersionAsync(app, envelope)), HttpStatusCode.Conflict, ItemCodes.InUse);

        Assert.Equal(1, blocker.GetProperty("count").GetInt32());
        var named = Assert.Single(blocker.GetProperty("items").EnumerateArray());
        Assert.Equal((goal.Value, "Voyage"), (named.GetProperty("id").GetGuid(), named.GetProperty("name").GetString()));
        Assert.Equal(HttpStatusCode.OK, await EnvelopeStatusAsync(app, envelope));
        await using var goals = PostgresFixture.NewGoalsContext(app.ConnectionString);
        Assert.Single(goals.Funding.Where(f => f.EnvelopeId == envelope.Value));
    }

    [Fact]
    public async Task Hidden_goal_blocks_without_its_name()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", Visibility.Shared, (app.AdultA, 5000), (app.AdultB, 5000));
        await app.NewGoalAsync("Cadeau", app.AdultA, Visibility.Private, funding: [(envelope, 5000)]);

        var blocker = Blocker(await PreviewAsync(app, app.AdultB, envelope), "goal_funding");

        Assert.Equal(1, blocker.GetProperty("count").GetInt32());
        Assert.Empty(blocker.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Closed_goal_funding_goes_with_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var goal = await app.NewGoalAsync("Voyage", app.AdultA, funding: [(envelope, 5000)]);
        await SendAsync(app, HttpMethod.Post, $"/api/v1/goals/{goal.Value}/close", null, HttpStatusCode.OK);

        var preview = await PreviewAsync(app, app.AdultA, envelope);
        var deleted = await DeleteAsync(app, envelope, preview.GetProperty("version").GetUInt32());

        Assert.Empty(preview.GetProperty("blockers").EnumerateArray());
        Assert.Equal(1, Removed(preview, "goal_funding_closed"));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await using var goals = PostgresFixture.NewGoalsContext(app.ConnectionString);
        Assert.Empty(goals.Funding.Where(f => f.EnvelopeId == envelope.Value));
        Assert.Single(goals.Goals.Where(g => g.ItemId == goal.Value));
    }

    [Fact]
    public async Task Active_transfer_blocks_and_names_the_other_envelope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var livret = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var pea = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        await TransferAsync(app, livret, pea);

        var blocker = Blocker(await PreviewAsync(app, app.AdultA, livret), "transfers");
        await AssertRefusedAsync(await DeleteAsync(app, livret, await VersionAsync(app, livret)), HttpStatusCode.Conflict, ItemCodes.InUse);

        Assert.Equal(1, blocker.GetProperty("count").GetInt32());
        var named = Assert.Single(blocker.GetProperty("items").EnumerateArray());
        Assert.Equal((pea.Value, "PEA"), (named.GetProperty("id").GetGuid(), named.GetProperty("name").GetString()));
        Assert.Equal(HttpStatusCode.OK, await EnvelopeStatusAsync(app, livret));
    }

    [Fact]
    public async Task Transfer_to_a_private_envelope_of_another_adult_blocks_without_its_name()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var livret = await app.NewEnvelopeAsync("Livret", Visibility.Shared, (app.AdultA, 5000), (app.AdultB, 5000));
        var pea = await app.NewEnvelopeAsync("PEA", app.AdultB, Visibility.Private);
        await SendAsync(app, HttpMethod.Post, "/api/v1/investments/transfers",
            new { from = livret.Value, to = pea.Value, occurredOn = "2025-06-15", amount = Eur("200.00") }, HttpStatusCode.Created, app.AdultB);

        var blocker = Blocker(await PreviewAsync(app, app.AdultA, livret), "transfers");

        Assert.Equal(1, blocker.GetProperty("count").GetInt32());
        Assert.Empty(blocker.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Voided_transfer_does_not_block()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var livret = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var pea = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var transfer = await TransferAsync(app, livret, pea);
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            var leg = await core.Flows.SingleAsync(f => f.ExternalRef == $"transfer:{transfer}" && f.ItemId == pea.Value, TestContext.Current.CancellationToken);
            await SendAsync(app, HttpMethod.Delete, $"{Envelopes}/{pea.Value}/flows/{leg.Id}", null, HttpStatusCode.OK);
        }

        var preview = await PreviewAsync(app, app.AdultA, livret);
        var deleted = await DeleteAsync(app, livret, preview.GetProperty("version").GetUInt32());

        Assert.Empty(preview.GetProperty("blockers").EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await EnvelopeStatusAsync(app, pea));
    }

    [Fact]
    public async Task Bank_account_is_detached()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("Compte courant", app.AdultA, Visibility.Shared);
        Guid account;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var now = NidApp.GoldenCaseToday.AtMidnight().InUtc().ToInstant();
            var connections = sp.GetRequiredService<IConnectionStore>();
            var connection = await connections.CreatePendingAsync(app.AdultA, "Banque", "FR", [1, 2, 3], now + Duration.FromMinutes(15), ct);
            await connections.ActivateAsync(connection, "session", now + Duration.FromDays(180), ct);
            account = (await sp.GetRequiredService<IAccountStore>()
                .UpsertFromSessionAsync(connection, [new SessionAccount("uid", "hash", "FR7630006000011234567890189", "Compte", "CACC", "EUR")], ct))[0].Id;
            var db = sp.GetRequiredService<BankingDbContext>();
            var row = await db.Accounts.SingleAsync(a => a.Id == account, ct);
            (row.EnvelopeItemId, row.AssociatedOn, row.AssociatedByPersonId) = (envelope.Value, new LocalDate(2025, 10, 1), app.AdultA.Value);
            await db.SaveChangesAsync(ct);
        }

        var preview = await PreviewAsync(app, app.AdultA, envelope);
        var deleted = await DeleteAsync(app, envelope, preview.GetProperty("version").GetUInt32());

        Assert.Equal(1, Removed(preview, "bank_account"));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await using var banking = PostgresFixture.NewBankingContext(app.ConnectionString);
        Assert.Null((await banking.Accounts.SingleAsync(a => a.Id == account, ct)).EnvelopeItemId);
    }

    [Fact]
    public async Task Stale_version_answers_409()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var version = await VersionAsync(app, envelope);
        await SendAsync(app, HttpMethod.Patch, $"{Envelopes}/{envelope.Value}", new { displayName = "PEA Alice" }, HttpStatusCode.OK);

        await AssertRefusedAsync(await DeleteAsync(app, envelope, version), HttpStatusCode.Conflict, ItemCodes.Changed);

        Assert.Equal(HttpStatusCode.OK, await EnvelopeStatusAsync(app, envelope));
    }

    [Fact]
    public async Task Missing_version_answers_422()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Envelopes}/{envelope.Value}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("version", (await JsonAsync(response)).GetProperty("errors")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Reader_gets_403_and_stranger_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var secret = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Private);

        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Envelopes}/{shared.Value}/deletion")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Envelopes}/{shared.Value}?version=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Envelopes}/{secret.Value}/deletion")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Envelopes}/{secret.Value}?version=1")).StatusCode);
    }

    [Fact]
    public async Task Write_direct_token_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var version = await VersionAsync(app, envelope);
        var token = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Direct);

        await AssertRefusedAsync(await app.BearerAsync(token.Secret, HttpMethod.Delete, $"{Envelopes}/{envelope.Value}?version={version}"),
            HttpStatusCode.Forbidden, BrowserOnlyCodes.Required);

        Assert.Equal(HttpStatusCode.OK, await EnvelopeStatusAsync(app, envelope));
    }
}
