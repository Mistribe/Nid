using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>#395 to #398 on the wire (the app clock is fixed at 2025-10-15).</summary>
public class SpendingEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    public sealed record Money(string Amount, string Currency);
    public sealed record Spent(string Since, Money? Total, Money? Last12Months, bool Partial);
    public sealed record Saved(string Since, Money? Total, Money LastPrice, bool Partial);
    public sealed record Detail(Spent? Spent, Saved? Saved);
    public sealed record Summary(string DisplayName, Spent? Spent);
    public sealed record ListResponse(IReadOnlyList<Summary> Items);
    public sealed record YearRow(int Year, Money? Paid, Money? Upcoming);
    public sealed record SavedItem(Guid ItemId, Money? Last12Months);
    public sealed record Savings(Money? Last12Months, IReadOnlyList<SavedItem> Items);
    public sealed record Rise(Guid ItemId, Money? YearlyDifference, bool PromoEnded, decimal? ChangePct, RealPercentView? Real);
    public sealed record Rises(Money? YearlyTotal, IReadOnlyList<Rise> Items);
    public sealed record Overview(IReadOnlyList<YearRow> Years, Savings Savings, Rises PriceRises);

    private static async Task PhaseAsync(NidApp app, ItemId id, string startsOn, string? endsOn, string amount, PersonId? by = null, string currency = "EUR")
        => (await app.RawAsync(by ?? app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn,
            endsOn,
            price = new { amount, currency },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();

    [Fact]
    public async Task Detail_list_and_overview_carry_the_spent_saved_years_and_rises()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var running = await app.NewSubscriptionAsync("Running", app.AdultA, Visibility.Shared);
        await PhaseAsync(app, running, "2025-01-01", "2025-03-01", "10.00");
        await PhaseAsync(app, running, "2025-03-01", null, "12.00");
        var ended = await app.NewSubscriptionAsync("Ended", app.AdultA, Visibility.Shared);
        await PhaseAsync(app, ended, "2025-01-01", "2025-06-01", "5.00");

        var detail = await app.GetAsync<Detail>(app.AdultA, $"/api/v1/subscriptions/{running.Value}");
        // January and February at 10, March to October at 12.
        Assert.Equal(new Spent("2025-01-01", new Money("116.00", "EUR"), new Money("116.00", "EUR"), false), detail.Spent);
        Assert.Null(detail.Saved);

        var gone = await app.GetAsync<Detail>(app.AdultA, $"/api/v1/subscriptions/{ended.Value}");
        Assert.Equal(new Saved("2025-06-01", new Money("25.00", "EUR"), new Money("5.00", "EUR"), false), gone.Saved);

        var list = await app.GetAsync<ListResponse>(app.AdultA, "/api/v1/subscriptions?status=all");
        Assert.Equal("25.00", list.Items.Single(i => i.DisplayName == "Ended").Spent!.Total!.Amount);

        var overview = await app.GetAsync<Overview>(app.AdultA, "/api/v1/subscriptions/overview");
        var year = Assert.Single(overview.Years);
        Assert.Equal((2025, "141.00", "24.00"), (year.Year, year.Paid!.Amount, year.Upcoming!.Amount));
        Assert.Equal((ended.Value, "25.00"), (Assert.Single(overview.Savings.Items).ItemId, overview.Savings.Last12Months!.Amount));
        var rise = Assert.Single(overview.PriceRises.Items);
        Assert.Equal((running.Value, "24.00", false), (rise.ItemId, rise.YearlyDifference!.Amount, rise.PromoEnded));
    }

    [Fact]
    public async Task A_partner_s_shared_successor_of_the_same_provider_cancels_the_saving_in_the_detail_and_the_overview()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedProviderAsync("acme", "Acme", ProviderOrigin.Custom, app.AdultA);
        var ended = await app.NewSubscriptionAsync("Ended", app.AdultA, Visibility.Shared);
        await PhaseAsync(app, ended, "2025-01-01", "2025-06-01", "5.00");
        var successor = await app.NewSubscriptionAsync("Successor", app.AdultB, Visibility.Shared);
        await PhaseAsync(app, successor, "2025-06-01", null, "6.00", app.AdultB);
        await app.LinkProviderAsync(ended, "acme");
        await app.LinkProviderAsync(successor, "acme");

        Assert.Null((await app.GetAsync<Detail>(app.AdultA, $"/api/v1/subscriptions/{ended.Value}")).Saved);
        foreach (var scope in new[] { "household", "me" })
            Assert.Empty((await app.GetAsync<Overview>(app.AdultA, $"/api/v1/subscriptions/overview?scope={scope}")).Savings.Items);
    }

    private static readonly YearMonth First = new(2023, 9), Last = new(2025, 9);

    private static async Task<Rise> RiseAsync(NidApp app, string currency, bool seed = true)
    {
        if (seed) await app.SeedCpiAsync(First, Last, 1.002m);
        var id = await app.NewSubscriptionAsync("Streaming", app.AdultA, Visibility.Shared);
        await PhaseAsync(app, id, "2023-09-01", "2025-09-01", "10.00", currency: currency);
        await PhaseAsync(app, id, "2025-09-01", null, "11.00", currency: currency);
        return Assert.Single((await app.GetAsync<Overview>(app.AdultA, "/api/v1/subscriptions/overview")).PriceRises.Items);
    }

    [Fact]
    public async Task Price_rise_gives_its_change_and_real_change()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var rise = await RiseAsync(app, "EUR");

        var factor = 1m; // cpi(2025-09) / cpi(2023-09): 24 months at +0.2 %
        for (var n = 0; n < 24; n++) factor *= 1.002m;
        Assert.Equal(10.0m, rise.ChangePct);
        Assert.Equal(Math.Round((1.10m / factor - 1) * 100, 1, MidpointRounding.AwayFromZero), rise.Real!.Percent);
        Assert.Equal("2025-09", rise.Real!.InflationTo);
        Assert.NotNull(rise.Real.Percent);
    }

    [Fact]
    public async Task Price_rise_without_a_cpi_series_keeps_its_change_and_says_unavailable()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var rise = await RiseAsync(app, "EUR", seed: false);

        Assert.Equal(10.0m, rise.ChangePct);
        Assert.Equal((null, "unavailable"), (rise.Real!.Percent, rise.Real.Missing));
    }

    [Fact]
    public async Task Price_rise_in_chf_has_no_real_change()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var rise = await RiseAsync(app, "CHF");

        Assert.Equal(10.0m, rise.ChangePct);
        Assert.Equal((null, "currency"), (rise.Real!.Percent, rise.Real.Missing));
    }
}
