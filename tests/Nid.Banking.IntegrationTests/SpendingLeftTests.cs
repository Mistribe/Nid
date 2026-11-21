using System.Globalization;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

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
        // A period: none of them.
        var period = await SpendingTests.SpendingAsync(app, app.AdultA, "?scope=me&from=2024-11&to=2025-10");
        Assert.Equal((null, null, null, null, null),
            (Text(period, "income"), Text(period, "committed"), Text(period, "everyday"), Text(period, "left"), Text(period, "leftEstimate")));
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
}
