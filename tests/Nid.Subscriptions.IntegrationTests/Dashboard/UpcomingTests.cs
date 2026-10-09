using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Dashboard;

[Collection("postgres")]
public class UpcomingTests(PostgresFixture fixture)
{
    private static async Task<JsonElement> DashboardAsync(NidApp app, PersonId reader, string scope = "household")
        => JsonDocument.Parse(await app.GetRawAsync(reader, $"/api/v1/dashboard?scope={scope}")).RootElement;

    private static async Task<ItemId> SubscriptionAsync(
        NidApp app, string name, string startsOn, int count, string unit, string price = "69.90", string currency = "EUR",
        string? endsOn = null, PersonId? owner = null, Visibility visibility = Visibility.Shared)
    {
        var who = owner ?? app.AdultA;
        var id = await app.NewSubscriptionAsync(name, who, visibility);
        (await app.RawAsync(who, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn, endsOn,
            price = new { amount = price, currency },
            every = new { count, unit },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        return id;
    }

    private static async Task<ItemId> SourceAsync(NidApp app, string name, string label, string startsOn, string? endsOn)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/income/sources", new
        {
            displayName = name, kind = "salary", currency = "EUR", visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            phases = new[]
            {
                new { label, startsOn, endsOn, netAmount = new { amount = "2000", currency = "EUR" }, every = new { count = 1, unit = "month" } },
            },
        });
        response.EnsureSuccessStatusCode();
        return new ItemId(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid());
    }

    private static List<JsonElement> Upcoming(JsonElement view) => view.GetProperty("upcoming").EnumerateArray().ToList();

    [Fact]
    public async Task A_yearly_renewal_is_upcoming_and_a_monthly_one_is_not()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var prime = await SubscriptionAsync(app, "Amazon Prime", "2024-11-10", 1, "year");
        await SubscriptionAsync(app, "Netflix", "2025-01-05", 1, "month", "13.49");
        await SubscriptionAsync(app, "Lointain", "2025-01-01", 1, "year");

        var events = Upcoming(await DashboardAsync(app, app.AdultA));

        var only = Assert.Single(events);
        Assert.Equal("subscription_renewal", only.GetProperty("kind").GetString());
        Assert.Equal("2025-11-10", only.GetProperty("on").GetString());
        Assert.Equal(prime.Value, only.GetProperty("id").GetGuid());
        Assert.Equal("subscriptions", only.GetProperty("module").GetString());
        Assert.Equal("Amazon Prime", only.GetProperty("name").GetString());
        Assert.Equal("69.90", only.GetProperty("amount").GetProperty("amount").GetString());
    }

    [Fact]
    public async Task The_window_includes_its_last_day()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SubscriptionAsync(app, "Dernier jour", "2024-12-14", 1, "year");
        await SubscriptionAsync(app, "Lendemain", "2024-12-15", 1, "year");

        var events = Upcoming(await DashboardAsync(app, app.AdultA));

        Assert.Equal(new[] { "Dernier jour" }, events.Select(e => e.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task A_renewal_keeps_its_own_currency()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SubscriptionAsync(app, "VPN", "2024-11-01", 1, "year", "99.00", "USD"); // no rate in the test database

        var amount = Upcoming(await DashboardAsync(app, app.AdultA)).Single().GetProperty("amount");

        Assert.Equal("99.00", amount.GetProperty("amount").GetString());
        Assert.Equal("USD", amount.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task A_subscription_ending_without_renewal_expires_whatever_its_cycle()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SubscriptionAsync(app, "Fin", "2025-09-01", 1, "month", "5.00", endsOn: "2025-11-01");

        var expiry = Upcoming(await DashboardAsync(app, app.AdultA)).Single();

        Assert.Equal("subscription_expiry", expiry.GetProperty("kind").GetString());
        Assert.Equal("2025-11-01", expiry.GetProperty("on").GetString());
        Assert.Equal(JsonValueKind.Null, expiry.GetProperty("amount").ValueKind);
    }

    [Fact]
    public async Task An_income_period_ending_soon_is_upcoming_and_its_missing_follow_up_is_to_do()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await SourceAsync(app, "Employeur", "CDD", "2025-01-01", "2025-11-30");
        await SourceAsync(app, "Lointain", "CDI", "2025-01-01", "2026-06-30");

        var view = await DashboardAsync(app, app.AdultA);

        var end = Upcoming(view).Single();
        Assert.Equal("income_phase_end", end.GetProperty("kind").GetString());
        Assert.Equal("2025-11-30", end.GetProperty("on").GetString());
        Assert.Equal("CDD", end.GetProperty("label").GetString());
        Assert.Equal("income", end.GetProperty("module").GetString());
        var todo = view.GetProperty("todo").EnumerateArray().Single();
        Assert.Equal("income_gap_soon", todo.GetProperty("kind").GetString());
        Assert.Equal(source.Value, todo.GetProperty("id").GetGuid());
        Assert.Equal("CDD", todo.GetProperty("label").GetString());
        Assert.Equal("2025-11-30", todo.GetProperty("on").GetString());
    }

    [Fact]
    public async Task To_do_lists_stale_values_oldest_first_then_envelopes_without_value()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var older = await app.NewEnvelopeAsync("Plus ancien", app.AdultA, Visibility.Shared);
        var old = await app.NewEnvelopeAsync("Ancien", app.AdultA, Visibility.Shared);
        await app.NewEnvelopeAsync("Vide", app.AdultA, Visibility.Shared);
        foreach (var (id, asOf) in new[] { (old, "2025-08-01"), (older, "2025-07-01") })
            Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post,
                $"/api/v1/investments/envelopes/{id.Value}/valuations",
                new { asOf, amount = new { amount = "10.00", currency = "EUR" }, note = (string?)null })).StatusCode);

        var todo = (await DashboardAsync(app, app.AdultA)).GetProperty("todo").EnumerateArray().ToList();

        Assert.Equal(new[] { "stale_value", "stale_value", "no_value" }, todo.Select(t => t.GetProperty("kind").GetString()));
        Assert.Equal(new[] { "Plus ancien", "Ancien", "Vide" }, todo.Select(t => t.GetProperty("name").GetString()));
        Assert.Equal("2025-07-01", todo[0].GetProperty("on").GetString());
        Assert.Equal(JsonValueKind.Null, todo[2].GetProperty("on").ValueKind);
    }

    [Theory]
    [InlineData("household")]
    [InlineData("me")]
    public async Task The_other_adults_private_items_bring_no_event_and_no_task(string scope)
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SubscriptionAsync(app, "Secret", "2024-11-10", 1, "year", owner: app.AdultB, visibility: Visibility.Private);
        await app.NewEnvelopeAsync("Secret", app.AdultB, Visibility.Private);

        var view = await DashboardAsync(app, app.AdultA, scope);

        Assert.Equal(0, view.GetProperty("upcoming").GetArrayLength());
        Assert.Equal(0, view.GetProperty("todo").GetArrayLength());
    }
}
