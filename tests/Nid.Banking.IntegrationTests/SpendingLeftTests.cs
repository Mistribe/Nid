using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Banking.IntegrationTests;

/// <summary>Issue #425: Accueil's « Reste réel » and its parts on the spending view, and how the month will end. Today is 2025-10-15.</summary>
public class SpendingLeftTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static decimal Number(JsonElement element, string property) => decimal.Parse(Text(element, property)!, CultureInfo.InvariantCulture);

    /// <summary>A dashboard money figure's amount (<paramref name="inner"/>: a figure's total), null when absent.</summary>
    private static string? Amount(JsonElement dashboard, string property, string? inner = null)
    {
        var figure = dashboard.GetProperty(property);
        if (inner is not null && figure.ValueKind == JsonValueKind.Object) figure = figure.GetProperty(inner);
        return figure.ValueKind == JsonValueKind.Object ? figure.GetProperty("amount").GetString() : null;
    }

    [Fact]
    public async Task The_month_shows_accueil_s_figures_and_the_current_one_how_it_ends_against_the_usual_everyday_month()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Compte"));
        var account = (await SpendingTests.AccountsAsync(app, app.AdultA))["Compte"];
        // July to September: three whole months of 300 everyday spending; September also pays a subscription linked to its item.
        await SpendingTests.LineAsync(app, account, new(2025, 7, 1), -300m, "food.groceries");
        await SpendingTests.LineAsync(app, account, new(2025, 8, 10), -300m, "food.groceries");
        await SpendingTests.LineAsync(app, account, new(2025, 9, 10), -300m, "food.groceries");
        await DashboardSpendingTests.LinkAsync(app, await SpendingTests.LineAsync(app, account, new(2025, 9, 12), -600m, "leisure.subscriptions"));
        // October so far: 100 everyday, and a tax payment the margin already counts.
        await SpendingTests.LineAsync(app, account, new(2025, 10, 3), -100m, "food.groceries");
        await SpendingTests.LineAsync(app, account, new(2025, 10, 6), -40m, "taxes");

        // No income declared: unknown, never 0, and nothing left to say.
        var unknown = await SpendingTests.SpendingAsync(app, app.AdultA, "?scope=me");
        Assert.Equal((null, null, null), (Text(unknown, "income"), Text(unknown, "left"), Text(unknown, "leftEstimate")));
        Assert.Equal("100.00", Text(unknown, "everyday"));

        await app.NewIncomeSourceAsync("Salaire Alice", Visibility.Shared, (app.AdultA, 10_000));
        var streaming = await app.NewSubscriptionAsync("Streaming", app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{streaming.Value}/phases", new
        {
            startsOn = "2025-01-01", price = new { amount = "20.00", currency = "EUR" }, every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        foreach (var scope in new[] { "me", "household" })
        {
            // The same figures as Accueil, for the same scope.
            var dashboard = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/dashboard?scope={scope}")).RootElement;
            var spending = await SpendingTests.SpendingAsync(app, app.AdultA, $"?scope={scope}");
            // Accueil reads no commitment as « aucun engagement » (no total); its margin counts it as 0, as the spending view says.
            Assert.Equal(
                (Amount(dashboard, "income", "total"), Amount(dashboard, "commitments", "total") ?? "0.00", Amount(dashboard, "everydaySpending"), Amount(dashboard, "left")),
                (Text(spending, "income"), Text(spending, "committed"), Text(spending, "everyday"), Text(spending, "left")));
            Assert.Equal(decimal.Parse(Amount(dashboard, "margin")!, CultureInfo.InvariantCulture), Number(spending, "income") - Number(spending, "committed"));
        }

        var october = await SpendingTests.SpendingAsync(app, app.AdultA, "?scope=me");
        Assert.Equal("1000.00", Text(october, "income"));
        Assert.Equal("20.00", Text(october, "committed"));
        // Everyday leaves out the tax and the linked lines; the total keeps them.
        Assert.Equal(("140.00", "100.00"), (Text(october, "total"), Text(october, "everyday")));
        // The Mois habituel counts the linked subscription (1 500 over 3); the estimate takes the usual everyday month (300), not it.
        Assert.Equal("500.00", Text(october, "average"));
        var margin = Number(october, "income") - Number(october, "committed");
        Assert.Equal(margin - 100m, Number(october, "left"));
        Assert.Equal(margin - 300m, Number(october, "leftEstimate"));

        // A past month is over: its figures, no estimate.
        var september = await SpendingTests.SpendingAsync(app, app.AdultA, "?scope=me&month=2025-09");
        Assert.Equal("300.00", Text(september, "everyday"));
        Assert.NotNull(Text(september, "left"));
        Assert.Null(Text(september, "leftEstimate"));
        // A period is no month to end: no estimate (#428 gives it the other figures).
        var period = await SpendingTests.SpendingAsync(app, app.AdultA, "?scope=me&from=2024-11&to=2025-10");
        Assert.Null(Text(period, "leftEstimate"));
    }

    [Fact]
    public async Task A_period_shows_each_month_as_its_month_view_and_adds_them_up_with_the_savings_rate()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Compte"));
        var account = (await SpendingTests.AccountsAsync(app, app.AdultA))["Compte"];
        await SpendingTests.LineAsync(app, account, new(2025, 8, 10), -300m, "food.groceries");
        await SpendingTests.LineAsync(app, account, new(2025, 9, 10), -200m, "food.groceries");
        await SpendingTests.LineAsync(app, account, new(2025, 10, 3), -100m, "food.groceries");
        const string query = "?scope=me&from=2025-07&to=2025-10";

        // No income declared: unknown each month and over the period, and no savings rate; the spending is still added up.
        var unknown = await SpendingTests.SpendingAsync(app, app.AdultA, query);
        Assert.All(unknown.GetProperty("months").EnumerateArray(), m => Assert.Null(Text(m, "income")));
        Assert.Equal((null, null, "600.00"), (Text(unknown, "income"), Text(unknown, "left"), Text(unknown, "everyday")));
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("savingsRate").ValueKind);

        // A raise from September: 1 000 a month, then 1 500.
        var salary = await app.NewIncomeSourceAsync("Salaire Alice", app.AdultA);
        foreach (var phase in new object[]
                 {
                     new { label = "Salaire", startsOn = "2025-01-01", endsOn = "2025-09-01", netAmount = new { amount = "1000", currency = "EUR" }, every = new { count = 1, unit = "month" } },
                     new { label = "Salaire", startsOn = "2025-09-01", netAmount = new { amount = "1500", currency = "EUR" }, every = new { count = 1, unit = "month" } },
                 })
            (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/income/sources/{salary.Value}/phases", phase)).EnsureSuccessStatusCode();

        var period = await SpendingTests.SpendingAsync(app, app.AdultA, query);
        var months = period.GetProperty("months").EnumerateArray().ToList();
        Assert.Equal(["1000.00", "1000.00", "1500.00", "1500.00"], months.Select(m => Text(m, "income")));
        Assert.Equal(["0.00", "300.00", "200.00", "100.00"], months.Select(m => Text(m, "everyday")));
        // Each month is its month view's, the current one included.
        foreach (var m in months)
        {
            var alone = await SpendingTests.SpendingAsync(app, app.AdultA, $"?scope=me&month={Text(m, "month")}");
            Assert.Equal((Text(alone, "income"), Text(alone, "committed"), Text(alone, "everyday"), Text(alone, "left")),
                (Text(m, "income"), Text(m, "committed"), Text(m, "everyday"), Text(m, "left")));
        }

        // The period adds its months up; the savings rate is what is left of the income, to 0.1 %.
        decimal Total(string property) => months.Sum(m => Number(m, property));
        Assert.Equal((5000m, Total("committed"), 600m, Total("left")),
            (Number(period, "income"), Number(period, "committed"), Number(period, "everyday"), Number(period, "left")));
        Assert.Equal(Number(period, "income") - Number(period, "committed") - Number(period, "everyday"), Number(period, "left"));
        Assert.Equal(Math.Round(Number(period, "left") / 5000m * 100m, 1, MidpointRounding.AwayFromZero), period.GetProperty("savingsRate").GetDecimal());
        Assert.Null(Text(period, "leftEstimate"));
    }

    [Fact]
    public async Task Without_a_usual_month_there_is_no_estimate_and_a_month_above_it_ends_where_it_stands()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Compte"));
        var account = (await SpendingTests.AccountsAsync(app, app.AdultA))["Compte"];
        await app.NewIncomeSourceAsync("Salaire Alice", Visibility.Shared, (app.AdultA, 10_000));
        await SpendingTests.LineAsync(app, account, new(2025, 9, 10), -50m, "food.groceries");
        await SpendingTests.LineAsync(app, account, new(2025, 10, 3), -400m, "food.groceries");

        // One month of history: no Mois habituel, so no estimate.
        var early = await SpendingTests.SpendingAsync(app, app.AdultA, "?scope=me");
        Assert.Null(Text(early, "average"));
        Assert.NotNull(Text(early, "left"));
        Assert.Null(Text(early, "leftEstimate"));

        // Three whole months of 50: the month already spent more than usual, so it ends where it stands.
        await SpendingTests.LineAsync(app, account, new(2025, 7, 1), -50m, "food.groceries");
        await SpendingTests.LineAsync(app, account, new(2025, 8, 10), -50m, "food.groceries");
        var october = await SpendingTests.SpendingAsync(app, app.AdultA, "?scope=me");
        Assert.Equal("50.00", Text(october, "average"));
        Assert.Equal(Text(october, "left"), Text(october, "leftEstimate"));
    }

    /// <summary>Issue #438: the tax lines each year is read for, by year.</summary>
    private sealed class CountingTax(ConcurrentDictionary<int, int> reads) : ITaxContribution
    {
        public Task<Result<IReadOnlyList<TaxLine>>> TaxLinesAsync(PersonId reader, VisibilityScope scope, int year, LocalDate today, CancellationToken ct)
        {
            reads.AddOrUpdate(year, 1, (_, n) => n + 1);
            return Task.FromResult(Result.Success<IReadOnlyList<TaxLine>>([]));
        }
    }

    [Fact]
    public async Task A_period_reads_the_year_before_once_and_each_month_still_matches_its_month_view()
    {
        var reads = new ConcurrentDictionary<int, int>();
        await using var app = await BankingApi.StartAsync(fixture, overrides: s => s.AddScoped<ITaxContribution>(_ => new CountingTax(reads)));
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Compte"));
        // A salary whose year before sets the PER ceiling of a large payment, a let property and a nanny whose months are projected.
        var salary = await app.NewIncomeSourceAsync("Salaire Alice", app.AdultA);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/income/sources/{salary.Value}/phases",
            new { label = "Salaire", startsOn = "2024-01-01", netAmount = new { amount = "6000", currency = "EUR" }, every = new { count = 1, unit = "month" } })).EnsureSuccessStatusCode();
        var per = await LoanApi.CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = "PER", kind = "per", currency = "EUR", visibility = "shared", openedOn = "2025-02-01",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            opening = new { occurredOn = "2025-02-01", amount = new { amount = "1000.00", currency = "EUR" } },
        }));
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{per}/flows",
            new { occurredOn = "2025-03-10", kind = "contribution", amount = new { amount = "20000", currency = "EUR" } })).EnsureSuccessStatusCode();
        var property = $"{PropertyApi.Properties}/{(await app.NewPropertyAsync("Locatif", Visibility.Shared, (app.AdultA, 10_000))).Value}";
        await LoanApi.CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{property}/tenancies",
            new { label = "Bail", startsOn = "2024-01-01", rentMonthly = "750", chargesMonthly = "0" }));
        var nanny = $"/api/v1/childcare/nannies/{await LoanApi.CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/childcare/nannies",
            new { name = "Garde à domicile", visibility = "shared", owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } } }))}";
        foreach (var month in new[] { "2024-11", "2025-02" })
            (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{nanny}/months/{month}", new { paid = "1000", cmg = "400" })).EnsureSuccessStatusCode();

        reads.Clear();
        var months = (await SpendingTests.SpendingAsync(app, app.AdultA, "?scope=me&from=2024-11&to=2025-10")).GetProperty("months").EnumerateArray().ToList();
        // Ten months of 2025 each estimate their year as read that month; the year before is read once (2024 is before the rules).
        Assert.Equal((10, 1), (reads.GetValueOrDefault(2025), reads.GetValueOrDefault(2024)));
        // The tax changes from month to month (the rents projected after each), and each month is still its month view's.
        Assert.True(months.Select(m => Text(m, "committed")).Distinct().Count() > 2);
        foreach (var m in months)
        {
            var alone = await SpendingTests.SpendingAsync(app, app.AdultA, $"?scope=me&month={Text(m, "month")}");
            Assert.Equal((Text(alone, "income"), Text(alone, "committed"), Text(alone, "left")), (Text(m, "income"), Text(m, "committed"), Text(m, "left")));
        }
    }
}
