using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Goals.IntegrationTests;

/// <summary>Issue #12: the annual review's goals, read at the period's start and end. Today is 2025-10-15.</summary>
public class GoalsReviewTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    // The 2024 review: from 31/12/2023 to 31/12/2024.
    private static readonly LocalDate Start = new(2023, 12, 31);
    private static readonly LocalDate End = new(2024, 12, 31);

    private static async Task<GoalsReviewPart> ReviewAsync(NidApp app, PersonId reader, LocalDate start, LocalDate end)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(reader);
        var read = await scope.ServiceProvider.GetRequiredService<IGoalsReview>().ReviewAsync(reader, VisibilityScope.Me, start, end, default);
        Assert.True(read.TryGetValue(out var part));
        return part!;
    }

    private static Task<GoalsReviewPart> ReviewAsync(NidApp app) => ReviewAsync(app, app.AdultA, Start, End);

    private static async Task SqlAsync(NidApp app, FormattableString sql)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CoreDbContext>().Database.ExecuteSqlAsync(sql, TestContext.Current.CancellationToken);
    }

    /// <summary>The fixed clock creates every item today: an older goal is backdated.</summary>
    private static Task CreatedOnAsync(NidApp app, ItemId goal, LocalDate on)
        => SqlAsync(app, $"update core.item set created_at = {on.AtMidnight().InUtc().ToInstant()} where id = {goal.Value}");

    private static Task ArchivedAtAsync(NidApp app, ItemId goal, Instant at)
        => SqlAsync(app, $"update core.item set archived_at = {at} where id = {goal.Value}");

    private static async Task<ItemId> OldGoalAsync(NidApp app, string name, IReadOnlyList<(ItemId Envelope, int ShareBp)>? funding = null)
    {
        var goal = await app.NewGoalAsync(name, app.AdultA, funding: funding); // target 10 000 EUR
        await CreatedOnAsync(app, goal, new LocalDate(2023, 1, 1));
        return goal;
    }

    private static async Task EntryAsync(NidApp app, ItemId goal, string on, string amount)
        => (await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/goals/{goal.Value}/progress/{on}", new { amount })).EnsureSuccessStatusCode();

    private static async Task ValuationAsync(NidApp app, ItemId envelope, string on, string amount, string currency = "EUR")
        => (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{envelope.Value}/valuations",
            new { asOf = on, amount = new { amount, currency } })).EnsureSuccessStatusCode();

    private static async Task RateAsync(NidApp app, LocalDate on, decimal eurPerUsd)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IFxRateStore>().WriteAsync([new FxRate(on, Currency.Eur, eurPerUsd, FxRateSource.Ecb)], default);
    }

    [Fact]
    public async Task A_free_goal_reads_its_entries_at_both_dates()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await OldGoalAsync(app, "Voyage");
        await EntryAsync(app, goal, "2023-06-01", "2000");
        await EntryAsync(app, goal, "2024-06-01", "5000");
        await EntryAsync(app, goal, "2025-03-01", "9000");

        var row = Assert.Single((await ReviewAsync(app)).Goals);

        Assert.Equal((goal, "Voyage", "savings", 10_000m), (row.Id, row.Name, row.Kind, row.Target));
        Assert.Equal((2000m, 5000m, 20m, 50m), (row.StartReached, row.EndReached, row.StartProgressPct, row.EndProgressPct));
        Assert.False(row.ReachedInPeriod);
        Assert.Null(row.ArchivedOn);
    }

    [Fact]
    public async Task A_funded_goal_reads_its_envelopes_at_both_dates()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var euros = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        await ValuationAsync(app, euros, "2023-12-01", "4000");
        await ValuationAsync(app, euros, "2024-11-30", "8000");
        await ValuationAsync(app, euros, "2025-10-01", "12000");
        var dollars = await app.NewEnvelopeAsync("Brokerage", app.AdultA, Visibility.Shared, "USD");
        await ValuationAsync(app, dollars, "2023-12-01", "1000", "USD");
        await ValuationAsync(app, dollars, "2024-12-01", "2000", "USD");
        await RateAsync(app, Start, 0.9m); // each date converts at its own rate
        await RateAsync(app, End, 0.8m);
        await OldGoalAsync(app, "Apport", [(euros, 5000), (dollars, 10_000)]);

        var part = await ReviewAsync(app);

        var row = Assert.Single(part.Goals);
        Assert.Equal((2000m + 900m, 4000m + 1600m), (row.StartReached, row.EndReached));
        Assert.Equal((29m, 56m), (row.StartProgressPct, row.EndProgressPct));
        Assert.Empty(part.MissingCurrencies);
    }

    [Fact]
    public async Task A_goal_reached_in_the_year_is_flagged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var reached = await OldGoalAsync(app, "Atteint en 2024");
        await EntryAsync(app, reached, "2023-06-01", "3000");
        await EntryAsync(app, reached, "2024-09-01", "10000");
        var before = await OldGoalAsync(app, "Atteint avant");
        await EntryAsync(app, before, "2023-06-01", "10000");

        var rows = (await ReviewAsync(app)).Goals.ToDictionary(r => r.Id);

        Assert.Equal((true, 100m), (rows[reached].ReachedInPeriod, rows[reached].EndProgressPct));
        Assert.False(rows[before].ReachedInPeriod);
    }

    [Fact]
    public async Task A_goal_created_in_the_year_has_no_start()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await app.NewGoalAsync("Nouveau", app.AdultA); // created today
        await EntryAsync(app, goal, "2025-05-01", "3000");

        var row = Assert.Single((await ReviewAsync(app, app.AdultA, new LocalDate(2024, 12, 31), NidApp.GoldenCaseToday)).Goals);

        Assert.Equal((null, null, 3000m, 30m), (row.StartReached, row.StartProgressPct, row.EndReached, row.EndProgressPct));
        Assert.Empty((await ReviewAsync(app)).Goals); // created after the 2024 review's end
    }

    [Fact]
    public async Task A_goal_archived_before_the_year_is_left_out()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var gone = await OldGoalAsync(app, "Abandonné");
        await ArchivedAtAsync(app, gone, Instant.FromUtc(2023, 6, 1, 10, 0));
        var archived = await OldGoalAsync(app, "Clos en 2024");
        await EntryAsync(app, archived, "2024-03-01", "4000");
        await EntryAsync(app, archived, "2024-09-01", "6000");
        await ArchivedAtAsync(app, archived, Instant.FromUtc(2024, 6, 15, 10, 0));

        var row = Assert.Single((await ReviewAsync(app)).Goals);

        Assert.Equal((archived, new LocalDate(2024, 6, 15)), (row.Id, row.ArchivedOn));
        Assert.Equal(4000m, row.EndReached); // read on its last counted day
    }

    [Fact]
    public async Task A_free_goal_exists_from_its_first_entry()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await app.NewGoalAsync("Importé", app.AdultA); // created today, its entries older
        await EntryAsync(app, goal, "2023-06-01", "2000");
        await EntryAsync(app, goal, "2024-06-01", "5000");

        var row = Assert.Single((await ReviewAsync(app)).Goals);

        Assert.Equal((goal, 2000m, 5000m), (row.Id, row.StartReached, row.EndReached));
    }

    [Fact]
    public async Task A_goal_created_in_the_year_and_reached_is_flagged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var goal = await app.NewGoalAsync("Nouveau", app.AdultA); // created today
        await EntryAsync(app, goal, "2025-05-01", "10000");

        var row = Assert.Single((await ReviewAsync(app, app.AdultA, new LocalDate(2024, 12, 31), NidApp.GoldenCaseToday)).Goals);

        Assert.Equal((null, 100m, true), (row.StartReached, row.EndProgressPct, row.ReachedInPeriod));
    }

    private static async Task UsdLoanAsync(NidApp app)
        => (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/realestate/loans", new
        {
            name = "Prêt US", currency = "USD", visibility = "shared",
            borrowers = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            principal = "10000", disbursedOn = "2025-01-01", firstPaymentDate = "2025-01-15", totalPayments = 24,
            rounding = "half_up", firstPeriod = "full",
            phases = new[] { new { seq = 1, mode = "annuity", annualRate = "0.01", rateType = "fixed" } },
        })).EnsureSuccessStatusCode(); // no USD rate

    private static async Task<ItemId> EmergencyFundAsync(NidApp app)
    {
        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/goals", new
        {
            name = "Précaution", kind = "emergency_fund", months = 6, visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } }, funding = Array.Empty<object>(),
        });
        created.EnsureSuccessStatusCode();
        return new ItemId(JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task An_emergency_fund_reads_its_target_from_todays_commitments()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewMonthlyAsync(app.AdultA, 50m);
        await UsdLoanAsync(app); // left out of the target, as the goal page, and named
        var goal = await EmergencyFundAsync(app);
        await EntryAsync(app, goal, "2025-05-01", "150");

        var part = await ReviewAsync(app, app.AdultA, new LocalDate(2024, 12, 31), NidApp.GoldenCaseToday);

        var row = Assert.Single(part.Goals);
        Assert.Equal(("emergency_fund", 300m, 50m), (row.Kind, row.Target, row.EndProgressPct));
        Assert.Equal(["USD"], part.MissingCurrencies);
    }

    [Fact]
    public async Task An_emergency_fund_whose_commitments_all_miss_a_rate_has_no_target()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await UsdLoanAsync(app);
        var goal = await EmergencyFundAsync(app);
        await EntryAsync(app, goal, "2025-05-01", "150");

        var part = await ReviewAsync(app, app.AdultA, new LocalDate(2024, 12, 31), NidApp.GoldenCaseToday);

        var row = Assert.Single(part.Goals);
        Assert.Equal((null, 150m, null), (row.Target, row.EndReached, row.EndProgressPct));
        Assert.Equal(["USD"], part.MissingCurrencies);
    }

    private static decimal? Amount(JsonElement goal, string field)
        => goal.GetProperty(field) is { ValueKind: JsonValueKind.Object } money ? decimal.Parse(money.GetProperty("amount").GetString()!, CultureInfo.InvariantCulture) : null;

    [Fact]
    public async Task The_current_year_reads_as_the_goal_page()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var free = await app.NewGoalAsync("Voyage", app.AdultA); // created today
        await EntryAsync(app, free, "2025-03-01", "4321.09");
        var envelope = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        await ValuationAsync(app, envelope, "2025-10-01", "2229.99");
        // Half of it is 1114.995: the page measures the progress on that (11.1 %), not on the cent-rounded 1115.00 (11.2 %).
        var funded = await app.NewGoalAsync("Apport", app.AdultA, funding: [(envelope, 5000)]);
        // Not covered: a missing rate, where the review nulls the reached amount and the page drops the envelope (ruling P4-R2).

        var rows = (await ReviewAsync(app, app.AdultA, new LocalDate(2024, 12, 31), NidApp.GoldenCaseToday)).Goals.ToDictionary(r => r.Id);

        foreach (var goal in new[] { free, funded })
        {
            var page = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/goals/{goal.Value}?scope=me")).RootElement.GetProperty("goal");
            var row = rows[goal];
            Assert.Equal((Amount(page, "target"), Amount(page, "reached"), page.GetProperty("progressPct").GetDecimal()),
                (row.Target, row.EndReached, row.EndProgressPct));
        }
        Assert.Equal((1115m, 11.1m), (rows[funded].EndReached, rows[funded].EndProgressPct));
        Assert.Null(rows[funded].StartReached); // P4-R1: a funded goal exists from its creation, in the year
    }

    [Fact]
    public async Task A_goal_with_an_envelope_hidden_from_the_reader_is_partial()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await app.NewEnvelopeAsync("Livret commun", app.AdultA, Visibility.Shared);
        await ValuationAsync(app, mine, "2025-10-01", "1000");
        var theirs = await app.NewEnvelopeAsync("Livret privé", app.AdultB, Visibility.Private);
        var goal = await app.NewGoalAsync("Apport", app.AdultB, funding: [(mine, 10_000), (theirs, 10_000)]);

        var row = Assert.Single((await ReviewAsync(app, app.AdultA, new LocalDate(2024, 12, 31), NidApp.GoldenCaseToday)).Goals);

        Assert.Equal((goal, true, 1000m), (row.Id, row.Partial, row.EndReached));
    }

    [Fact]
    public async Task An_envelope_without_a_rate_nulls_the_reached_amount()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var dollars = await app.NewEnvelopeAsync("Brokerage", app.AdultA, Visibility.Shared, "USD");
        await ValuationAsync(app, dollars, "2024-12-01", "1000", "USD");
        await OldGoalAsync(app, "Apport", [(dollars, 10_000)]);

        var part = await ReviewAsync(app);

        var row = Assert.Single(part.Goals);
        Assert.Equal((null, null), (row.EndReached, row.EndProgressPct));
        Assert.Equal(["USD"], part.MissingCurrencies);
    }
}
