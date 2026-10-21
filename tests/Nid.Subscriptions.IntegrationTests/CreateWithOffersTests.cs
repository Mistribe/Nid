using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>1E2b1 design §3.1: one request creates a subscription with its phases and purchases, or nothing.</summary>
public class CreateWithOffersTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static object Body(NidApp app, object[]? phases = null, object[]? purchases = null) => new
    {
        displayName = "Offre", visibility = "shared", autoRenew = true,
        owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        phases, purchases,
    };

    private static object Phase(string startsOn, string amount, int? maxCycles = null, string? endsOn = null) => new
    {
        startsOn, endsOn, price = new { amount, currency = "EUR" }, every = new { count = 1, unit = "month" },
        maxCycles, payer = new { type = "household" },
    };

    private static object Grant(int count, string source, Guid? subscriptionId = null, string? startsOn = null, string unit = "month")
        => new { subscriptionId, duration = new { count, unit }, source, startsOn };

    private static object Purchase(string kind, string amount, object[] grants, string? allocatedAmount = null,
        string date = "2025-01-01", string payer = "household") => new
    {
        date, label = kind, price = new { amount, currency = "EUR" }, payer = new { type = payer }, kind, grants, allocatedAmount,
    };

    private static async Task<Guid> Created(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<List<(string Path, string Code)>> ErrorsOf(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateArray()
            .Select(e => (e.GetProperty("path").GetString()!, e.GetProperty("code").GetString()!)).ToList();

    private static Task<ReadEndpointTests.DetailResponse> Detail(NidApp app, Guid id, string to = "2027-06-01")
        => app.GetAsync<ReadEndpointTests.DetailResponse>(app.AdultA, $"/api/v1/subscriptions/{id}?from=2025-01-01&to={to}");

    private static async Task<(int Items, int Subscriptions, int Purchases)> Counts(NidApp app)
    {
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        return (await core.Items.CountAsync(), await subs.Subscriptions.CountAsync(), await subs.Purchases.CountAsync());
    }

    [Fact]
    public async Task A_prepaid_term_with_free_months_covers_paid_then_bonus()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var id = await Created(await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", Body(app, purchases:
            [Purchase("prepaid_term", "179.86", [Grant(24, "paid"), Grant(3, "bonus")])])));

        var coverage = (await Detail(app, id)).Coverage.Select(c => (c.Start, c.EndBefore, c.Source)).ToList();
        Assert.Equal([("2025-01-01", "2027-01-01", "paid"), ("2027-01-01", "2027-04-01", "bonus")], coverage);
    }

    // Review Focus 2: the following phase charges only when the prepaid coverage ends.
    [Fact]
    public async Task A_phase_following_a_prepaid_term_charges_when_the_term_ends()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var id = await Created(await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", Body(app,
            phases: [Phase("2025-01-01", "9.99")],
            purchases: [Purchase("prepaid_term", "99", [Grant(12, "paid")])])));

        // Charges walk to the read horizon (unbounded here, unaffected by "to": constraints.md — no change to the
        // detail contract), so only the first two phase charges after the prepaid term are asserted: they are
        // what proves the phase did not charge while the term's coverage still ran.
        var charges = (await Detail(app, id, to: "2026-03-01")).Charges
            .Where(c => c.PurchaseId is null).Select(c => c.Date).Take(2).ToList();
        Assert.Equal(["2026-01-01", "2026-02-01"], charges);
    }

    [Fact]
    public async Task A_free_trial_then_a_phase_charges_after_the_trial()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var id = await Created(await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", Body(app,
            phases: [Phase("2025-01-01", "9.99")],
            purchases: [Purchase("prepaid_term", "0", [Grant(1, "trial", startsOn: "2025-01-01")])])));

        var detail = await Detail(app, id, to: "2025-04-01");
        Assert.Equal(("2025-01-01", "2025-02-01", "trial"), (detail.Coverage[0].Start, detail.Coverage[0].EndBefore, detail.Coverage[0].Source));
        Assert.Equal("2025-02-01", detail.Charges.First(c => c.PurchaseId is null).Date);
    }

    // Review Focus 3: a launch price ending on a short month's last day, then the normal price.
    [Fact]
    public async Task A_launch_price_then_the_normal_price_are_two_phases_that_do_not_overlap()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var id = await Created(await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", Body(app, phases:
            [Phase("2025-01-31", "0.99", maxCycles: 1, endsOn: "2025-02-28"), Phase("2025-02-28", "9.99")])));

        var charges = (await Detail(app, id, to: "2025-05-01")).Charges.Select(c => (c.Date, c.Price.Amount)).ToList();
        Assert.Equal(("2025-01-31", "0.99"), charges[0]);
        Assert.Equal(("2025-02-28", "9.99"), charges[1]);
    }

    [Fact]
    public async Task A_bundle_also_covers_an_existing_subscription_the_actor_writes()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var other = await app.NewSubscriptionAsync("other", app.AdultA, Visibility.Shared);

        await Created(await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", Body(app, purchases:
            [Purchase("bundle", "88.99", [Grant(3, "included"), Grant(3, "included", other.Value)], allocatedAmount: "20")])));

        var coverage = Assert.Single((await Detail(app, other.Value)).Coverage);
        Assert.Equal("included", coverage.Source);
    }

    // Review Focus 1: a target the actor may only read is refused like a missing one, before anything is written.
    [Fact]
    public async Task A_bundle_target_the_actor_cannot_write_refuses_the_whole_creation_like_a_missing_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var readOnly = await app.NewSubscriptionAsync("bob's", app.AdultB, Visibility.Shared);
        var before = await Counts(app);
        object Naming(Guid target) => Body(app, purchases:
            [Purchase("bundle", "10", [Grant(1, "included"), Grant(1, "included", target)], allocatedAmount: "0")]);

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", Naming(readOnly.Value));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal([("purchases[0].grants[1].subscriptionId", "subscriptions.not_found")], await ErrorsOf(refused));

        var bodyOf = async (HttpResponseMessage r) => JsonSerializer.Serialize((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors"));
        Assert.Equal(
            await bodyOf(await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", Naming(Guid.NewGuid()))),
            await bodyOf(await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", Naming(readOnly.Value))));
        Assert.Equal(before, await Counts(app));
    }

    [Fact]
    public async Task A_purchase_rule_refusal_is_located_and_creates_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var before = await Counts(app);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", Body(app, purchases:
            [Purchase("bundle", "10", [Grant(1, "included")], allocatedAmount: "12")]));

        Assert.Equal([("purchases[0].allocatedAmount", "subscriptions.allocated_range")], await ErrorsOf(response));
        Assert.Equal(before, await Counts(app));
    }

    [Fact]
    public async Task Overlapping_phases_are_refused_at_the_first_and_create_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var before = await Counts(app);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", Body(app,
            phases: [Phase("2025-01-01", "1"), Phase("2025-03-01", "2")]));

        Assert.Equal([("phases[0]", "subscriptions.phase_overlap")], await ErrorsOf(response));
        Assert.Equal(before, await Counts(app));
    }

    [Fact]
    public async Task Shape_errors_of_purchases_come_back_by_path()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", Body(app, purchases:
            [Purchase("gift", "0", [Grant(3, "free")], payer: "external"), Purchase("coupon", "1", [Grant(1, "paid")])]));

        Assert.Equal(["purchases[0].grants[0].source", "purchases[1].kind"],
            (await ErrorsOf(response)).Select(e => e.Path).Order(StringComparer.Ordinal));
    }
}
