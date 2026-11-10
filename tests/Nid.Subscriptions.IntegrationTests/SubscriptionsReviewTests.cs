using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>Issue #12: the yearly review's subscriptions (the app clock is fixed at 2025-10-15, so 2024 is a full year).</summary>
public class SubscriptionsReviewTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    public sealed record Money(string Amount, string Currency);
    public sealed record ListResponse(Money? HouseholdCashCostConverted);

    private static readonly LocalDate Start = new(2023, 12, 31), End = new(2024, 12, 31);

    private static async Task PhaseAsync(NidApp app, ItemId id, string startsOn, string? endsOn, string amount, string currency = "EUR")
        => (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn, endsOn,
            price = new { amount, currency },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();

    private static async Task<T> InScopeAsync<T>(NidApp app, Func<IServiceProvider, Task<T>> read)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
        return await read(scope.ServiceProvider);
    }

    private static Task<SubscriptionsReviewPart> ReviewAsync(NidApp app) => InScopeAsync(app, async sp =>
    {
        var review = await sp.GetRequiredService<ISubscriptionsReview>()
            .ReviewAsync(app.AdultA, VisibilityScope.Household, Start, End, TestContext.Current.CancellationToken);
        Assert.True(review.TryGetValue(out var part));
        return part!;
    });

    private static Task<bool> ArchiveAsync(NidApp app, ItemId id, LocalDate on) => InScopeAsync(app, async sp =>
        (await sp.GetRequiredService<IItemRegistry>().ArchiveAsync(id, Instant.FromUtc(on.Year, on.Month, on.Day, 10, 0), TestContext.Current.CancellationToken)).IsSuccess);

    [Fact]
    public async Task Cost_matches_the_list_over_the_same_dates()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var video = await app.NewSubscriptionAsync("Video", app.AdultA, Visibility.Shared);
        await PhaseAsync(app, video, "2024-01-01", "2024-07-01", "9.99");
        await PhaseAsync(app, video, "2024-07-01", null, "12.99");
        var lastDay = await app.NewSubscriptionAsync("Last day", app.AdultA, Visibility.Shared);
        await PhaseAsync(app, lastDay, "2024-01-31", null, "2.00"); // its December charge falls on the 31st
        var usd = await app.NewSubscriptionAsync("Usd", app.AdultA, Visibility.Shared);
        await PhaseAsync(app, usd, "2024-03-01", null, "10.00", "USD");
        var first = await app.NewSubscriptionAsync("Bundle one", app.AdultA, Visibility.Shared);
        var second = await app.NewSubscriptionAsync("Bundle two", app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{first.Value}/purchases", new
        {
            date = "2024-04-01", label = "two services", kind = "prepaid_term",
            price = new { amount = "30.00", currency = "EUR" },
            payer = new { type = "household" },
            grants = new[] { first, second }.Select(id => new
            {
                subscriptionId = id.Value, duration = new { count = 1, unit = "month" }, source = "paid",
            }).ToArray(),
        })).EnsureSuccessStatusCode();
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IFxRateStore>().WriteAsync(Enumerable.Range(3, 10)
                .Select(m => new FxRate(new LocalDate(2024, m, 1), Currency.Eur, m < 7 ? 0.9m : 0.8m, FxRateSource.Ecb)).ToList(), TestContext.Current.CancellationToken);

        var part = await ReviewAsync(app);
        // The list's `to` is the first day after the window ([from, to)): (31 Dec 2023, 31 Dec 2024] is from=2024-01-01&to=2025-01-01.
        var list = await app.GetAsync<ListResponse>(app.AdultA, "/api/v1/subscriptions?scope=household&from=2024-01-01&to=2025-01-01");

        // 6 × 9.99 + 6 × 12.99, 12 × 2, 4 × 9 + 6 × 8 (each USD charge at its date's rate), the shared purchase once.
        Assert.Equal(137.88m + 24m + 84m + 30m, part.Cost);
        Assert.Equal(part.Cost, decimal.Parse(list.HouseholdCashCostConverted!.Amount, CultureInfo.InvariantCulture));
        Assert.Empty(part.MissingCurrencies);
    }

    [Fact]
    public async Task A_missing_rate_leaves_no_cost_and_names_the_currency()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var usd = await app.NewSubscriptionAsync("Usd", app.AdultA, Visibility.Shared);
        await PhaseAsync(app, usd, "2024-03-01", null, "10.00", "USD");

        var part = await ReviewAsync(app);

        Assert.Null(part.Cost);
        Assert.Equal(["USD"], part.MissingCurrencies);
    }

    [Fact]
    public async Task An_archived_subscription_counts_before_its_archive_day()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var video = await app.NewSubscriptionAsync("Video", app.AdultA, Visibility.Shared);
        await PhaseAsync(app, video, "2024-01-01", "2024-07-01", "9.99");
        await PhaseAsync(app, video, "2024-07-01", null, "12.99");
        var music = await app.NewSubscriptionAsync("Music", app.AdultA, Visibility.Shared);
        await PhaseAsync(app, music, "2024-01-01", null, "5.00");
        Assert.True(await ArchiveAsync(app, video, new LocalDate(2024, 7, 1)));
        Assert.True(await ArchiveAsync(app, music, new LocalDate(2025, 3, 1)));

        var part = await ReviewAsync(app);

        Assert.Equal(6 * 9.99m + 12 * 5m, part.Cost); // the video's July charge falls on its archive day
        Assert.Empty(part.PriceChanges);
        Assert.Equal(["Music", "Video"], part.Started.Select(s => s.Name).Order(StringComparer.Ordinal));
        // Archived while running: ended on its archive day; the music, archived after the year, had not ended in it.
        Assert.Equal([("Video", new LocalDate(2024, 7, 1))], part.Ended.Select(e => (e.Name, e.On)));
    }

    [Fact]
    public async Task FirstCharge_is_the_earliest_charge()
    {
        await using var app = await NidApp.StartAsync(fixture);
        Task<LocalDate?> FirstAsync() => InScopeAsync(app, sp => sp.GetRequiredService<ISubscriptionsReview>()
            .FirstChargeAsync(app.AdultA, VisibilityScope.Household, TestContext.Current.CancellationToken));
        Assert.Null(await FirstAsync());

        var future = await app.NewSubscriptionAsync("Future", app.AdultA, Visibility.Shared);
        await PhaseAsync(app, future, "2026-01-01", null, "3.00");
        Assert.Null(await FirstAsync());

        var recent = await app.NewSubscriptionAsync("Recent", app.AdultA, Visibility.Shared);
        await PhaseAsync(app, recent, "2024-03-01", null, "4.00");
        var old = await app.NewSubscriptionAsync("Old", app.AdultA, Visibility.Shared);
        await PhaseAsync(app, old, "2023-05-01", null, "6.00");
        Assert.True(await ArchiveAsync(app, old, new LocalDate(2024, 1, 1)));

        Assert.Equal(new LocalDate(2023, 5, 1), await FirstAsync());
    }
}
