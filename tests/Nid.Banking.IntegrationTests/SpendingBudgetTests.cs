using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.Banking.IntegrationTests.SpendingTests;

namespace Nid.Banking.IntegrationTests;

/// <summary>#85 decisions 7 and 8: the budget blocks of a month in « Foyer ». Today is <see cref="NidApp.GoldenCaseToday"/> (2025-10-15, day 15 of 31).</summary>
public class SpendingBudgetTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static Guid Seed(string key) => CategorySeed.Find(key)!.Id;

    /// <summary>A household with one joint account (« Foyer » and both adults' « Moi »); its id.</summary>
    private static async Task<(NidApp App, Guid Joint)> StartAsync(PostgresFixture fixture)
    {
        var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Commun"));
        var joint = (await AccountsAsync(app, app.AdultA))["Commun"];
        await PatchAsync(app, app.AdultA, joint, new { envelopeId = (await app.NewEnvelopeAsync("Commun", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000))).Value });
        return (app, joint);
    }

    private static async Task BudgetAsync(NidApp app, string key, string? amount, string period = "month")
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{BankingApi.Root}/budgets/{Seed(key)}", new { amount, period });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The category (top-level or child) with this seed key, null when the view leaves it out.</summary>
    private static JsonElement? Find(JsonElement view, string key)
        => view.GetProperty("categories").EnumerateArray().SelectMany(c => c.GetProperty("children").EnumerateArray().Prepend(c))
            .Where(c => c.GetProperty("id").GetGuid() == Seed(key)).Select(c => (JsonElement?)c).SingleOrDefault();

    private static JsonElement? Budget(JsonElement view, string key)
        => Find(view, key) is { } c && c.TryGetProperty("budget", out var b) && b.ValueKind == JsonValueKind.Object ? b : null;

    private static string? S(JsonElement budget, string property)
        => budget.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>(planned, committed, source, period, left, expectedSoFar, state)</summary>
    private static (string?, string?, string?, string?, string?, string?, string?) Of(JsonElement? budget)
    {
        var b = Assert.NotNull(budget);
        return (S(b, "planned"), S(b, "committed"), S(b, "source"), S(b, "period"), S(b, "left"), S(b, "expectedSoFar"), S(b, "state"));
    }

    [Fact]
    public async Task A_subscription_due_this_month_fills_its_category_without_a_typed_budget_and_a_typed_one_wins()
    {
        var app = (await StartAsync(fixture)).App;
        await using var _ = app;
        // Due on the 1st of every month, no provider: « Loisirs › Abonnements ». Nothing spent yet, the budget still shows.
        await app.NewMonthlyAsync(app.AdultA, 9.99m);

        var view = await SpendingAsync(app, app.AdultB);
        Assert.Equal(("9.99", "9.99", "committed", "month", "9.99", "9.99", "on_track"), Of(Budget(view, "leisure.subscriptions")));
        // Due on the 1st, it is expected whole from then on. A child's commitment counts in its parent's too.
        Assert.Equal(("9.99", "9.99", "committed", "month", "9.99", "9.99", "on_track"), Of(Budget(view, "leisure")));

        Assert.False(view.GetProperty("incomplete").GetBoolean());
        Assert.False(view.GetProperty("budgetIncomplete").GetBoolean());

        // A budget with no amount follows the commitment, a month's even when the budget is yearly; a typed amount wins and shows the
        // commitment inside it.
        await BudgetAsync(app, "leisure.subscriptions", null, "year");
        Assert.Equal(("9.99", "9.99", "committed", "month", "9.99", "9.99", "on_track"),
            Of(Budget(await SpendingAsync(app, app.AdultA), "leisure.subscriptions")));
        await BudgetAsync(app, "leisure.subscriptions", "30");
        view = await SpendingAsync(app, app.AdultA);
        // 9.99 due on the 1st, then the 20.01 left spread: 9.99 + 20.01 × 15 ÷ 31.
        Assert.Equal(("30.00", "9.99", "typed", "month", "30.00", "19.67", "on_track"), Of(Budget(view, "leisure.subscriptions")));
        // A budget on a child takes the branch: the parent shows none.
        Assert.Null(Budget(view, "leisure"));
    }

    [Fact]
    public async Task A_commitment_with_no_rate_is_left_out_and_flags_the_month()
    {
        var app = (await StartAsync(fixture)).App;
        await using var _ = app;
        await app.NewMonthlyAsync(app.AdultA, 9.99m);
        // No GBP rate is ever loaded in a test host.
        var pounds = await app.NewSubscriptionAsync("Box GBP", app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{pounds.Value}/phases", new
        {
            startsOn = "2025-01-01",
            price = new { amount = "20", currency = "GBP" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();

        var view = await SpendingAsync(app, app.AdultA);
        // The budgets' flag, not the figures': every line had its rate.
        Assert.True(view.GetProperty("budgetIncomplete").GetBoolean());
        Assert.False(view.GetProperty("incomplete").GetBoolean());
        Assert.Equal(("9.99", "9.99", "committed", "month", "9.99", "9.99", "on_track"), Of(Budget(view, "leisure.subscriptions")));
        // « Moi » computes no commitment: nothing to flag.
        Assert.False((await SpendingAsync(app, app.AdultA, "?scope=me")).GetProperty("budgetIncomplete").GetBoolean());
    }

    [Fact]
    public async Task A_subscription_charged_on_the_1st_keeps_its_budget_on_track()
    {
        var (app, joint) = await StartAsync(fixture);
        await using var _ = app;
        await app.NewMonthlyAsync(app.AdultA, 9.99m);
        await LineAsync(app, joint, new(2025, 10, 1), -9.99m, "leisure.subscriptions");

        // Spread over the days, 4.83 would be expected by the 15th and the month « ahead »; the charge is due whole on the 1st.
        Assert.Equal(("9.99", "9.99", "committed", "month", "0.00", "9.99", "on_track"),
            Of(Budget(await SpendingAsync(app, app.AdultA), "leisure.subscriptions")));
    }

    [Fact]
    public async Task A_typed_budget_with_no_commitment_spreads_over_the_days()
    {
        var (app, joint) = await StartAsync(fixture);
        await using var _ = app;
        await BudgetAsync(app, "food.groceries", "300");
        // 300 × 15 ÷ 31 = 145.16; « ahead » past 159.68.
        await LineAsync(app, joint, new(2025, 10, 3), -160m, "food.groceries");

        Assert.Equal(("300.00", null, "typed", "month", "140.00", "145.16", "ahead"), Of(Budget(await SpendingAsync(app, app.AdultA), "food.groceries")));
    }

    [Fact]
    public async Task A_loan_instalment_inside_a_typed_budget_is_expected_from_its_due_day()
    {
        var (app, joint) = await StartAsync(fixture);
        await using var _ = app;
        // 800 on the 5th of every month, interest-free: « Logement › Loyer & prêt ».
        (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/realestate/loans", new
        {
            name = "Prêt maison", currency = "EUR", visibility = "shared",
            borrowers = new[] { new { personId = app.AdultA.Value, shareBp = 5_000 }, new { personId = app.AdultB.Value, shareBp = 5_000 } },
            principal = "19200", disbursedOn = "2025-01-01", firstPaymentDate = "2025-01-05", totalPayments = 24,
            rounding = "half_up", firstPeriod = "full",
            phases = new[] { new { seq = 1, mode = "annuity", annualRate = "0", rateType = "fixed" } },
        })).EnsureSuccessStatusCode();
        await BudgetAsync(app, "home", "1000");
        await LineAsync(app, joint, new(2025, 10, 5), -850m, "home.rent_loan");

        // 800 + 200 × 15 ÷ 31 = 896.77: on track, where 1 000 × 15 ÷ 31 = 483.87 would read « ahead ».
        Assert.Equal(("1000.00", "800.00", "typed", "month", "150.00", "896.77", "on_track"), Of(Budget(await SpendingAsync(app, app.AdultA), "home")));
    }

    [Fact]
    public async Task A_private_subscription_of_the_reader_is_no_household_commitment()
    {
        var app = (await StartAsync(fixture)).App;
        await using var _ = app;
        var mine = await app.NewSubscriptionAsync("Abonnement perso", app.AdultA, Visibility.Private);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{mine.Value}/phases", new
        {
            startsOn = "2025-01-01",
            price = new { amount = "9.99", currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();

        var view = await SpendingAsync(app, app.AdultA);
        Assert.Null(Find(view, "leisure.subscriptions"));
        Assert.Null(Find(view, "leisure"));
    }

    [Fact]
    public async Task A_yearly_budget_plans_a_twelfth_a_month()
    {
        var (app, joint) = await StartAsync(fixture);
        await using var _ = app;
        await LineAsync(app, joint, new(2025, 10, 3), -30m, "food.groceries");
        await BudgetAsync(app, "food.groceries", "1200", "year");

        Assert.Equal(("100.00", null, "typed", "year", "70.00", "48.39", "on_track"), Of(Budget(await SpendingAsync(app, app.AdultA), "food.groceries")));
    }

    [Fact]
    public async Task The_current_month_runs_at_its_pace_a_past_one_is_over_or_not_and_one_to_come_has_no_state()
    {
        var (app, joint) = await StartAsync(fixture);
        await using var _ = app;
        await BudgetAsync(app, "food.groceries", "310");
        await BudgetAsync(app, "transport.fuel", "310");
        await BudgetAsync(app, "leisure.sport", "100");
        // Expected so far 310 × 15 ÷ 31 = 150; « ahead » past 165.
        await LineAsync(app, joint, new(2025, 10, 3), -160m, "food.groceries");
        await LineAsync(app, joint, new(2025, 10, 3), -170m, "transport.fuel");
        await LineAsync(app, joint, new(2025, 10, 3), -120m, "leisure.sport");
        await LineAsync(app, joint, new(2025, 9, 3), -320m, "food.groceries");
        await LineAsync(app, joint, new(2025, 9, 3), -200m, "transport.fuel");

        var october = await SpendingAsync(app, app.AdultA);
        Assert.Equal(("310.00", null, "typed", "month", "150.00", "150.00", "on_track"), Of(Budget(october, "food.groceries")));
        Assert.Equal(("310.00", null, "typed", "month", "140.00", "150.00", "ahead"), Of(Budget(october, "transport.fuel")));
        Assert.Equal(("100.00", null, "typed", "month", "-20.00", "48.39", "over"), Of(Budget(october, "leisure.sport")));
        Assert.Null(Budget(october, "food"));

        var september = await SpendingAsync(app, app.AdultA, "?month=2025-09");
        Assert.Equal(("310.00", null, "typed", "month", "-10.00", null, "over"), Of(Budget(september, "food.groceries")));
        // 200 is past September's pace at any day but the month is over: only planned counts.
        Assert.Equal(("310.00", null, "typed", "month", "110.00", null, "on_track"), Of(Budget(september, "transport.fuel")));
        Assert.Equal(("100.00", null, "typed", "month", "100.00", null, "on_track"), Of(Budget(september, "leisure.sport")));

        var november = await SpendingAsync(app, app.AdultA, "?month=2025-11");
        Assert.Equal(("310.00", null, "typed", "month", "310.00", null, null), Of(Budget(november, "food.groceries")));
    }

    [Fact]
    public async Task Me_and_a_period_carry_no_budget()
    {
        var (app, joint) = await StartAsync(fixture);
        await using var _ = app;
        await LineAsync(app, joint, new(2025, 10, 3), -30m, "food.groceries");
        await BudgetAsync(app, "food.groceries", "300");
        await app.NewMonthlyAsync(app.AdultA, 9.99m);

        Assert.NotNull(Budget(await SpendingAsync(app, app.AdultA), "food.groceries"));
        foreach (var query in new[] { "?scope=me", "?from=2025-10&to=2025-10", "?from=2025-09&to=2025-10" })
        {
            var view = await SpendingAsync(app, app.AdultA, query);
            Assert.NotNull(Find(view, "food.groceries"));
            Assert.All(view.GetProperty("categories").EnumerateArray().SelectMany(c => c.GetProperty("children").EnumerateArray().Prepend(c)),
                c => Assert.True(!c.TryGetProperty("budget", out var b) || b.ValueKind == JsonValueKind.Null, query));
            // A category only committed to is not listed without its budget.
            Assert.Null(Find(view, "leisure.subscriptions"));
        }
    }

    [Fact]
    public async Task When_a_parent_and_its_child_both_have_a_budget_the_parent_s_counts()
    {
        var (app, joint) = await StartAsync(fixture);
        await using var _ = app;
        await LineAsync(app, joint, new(2025, 10, 3), -100m, "food.groceries");
        // The routes refuse the pair; an unarchive can still leave one.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var store = scope.ServiceProvider.GetRequiredService<ICategoryBudgetStore>();
            await store.SetAsync(new CategoryBudget(Seed("food"), 500m, BudgetPeriod.Month), TestContext.Current.CancellationToken);
            await store.SetAsync(new CategoryBudget(Seed("food.groceries"), 200m, BudgetPeriod.Month), TestContext.Current.CancellationToken);
        }

        var view = await SpendingAsync(app, app.AdultA);
        Assert.Equal(("500.00", null, "typed", "month", "400.00", "241.94", "on_track"), Of(Budget(view, "food")));
        Assert.NotNull(Find(view, "food.groceries"));
        Assert.Null(Budget(view, "food.groceries"));
    }
}
