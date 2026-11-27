using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Banking.IntegrationTests;

/// <summary>#85 decisions 3, 5 and 6: a month's or a period's spending by category, in the reference currency, against the 12-month average.
/// Today is <see cref="NidApp.GoldenCaseToday"/> (2025-10-15).</summary>
public class SpendingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Spending = $"{BankingApi.Root}/spending";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid Seed(string key) => CategorySeed.Find(key)!.Id;

    internal static async Task<Dictionary<string, Guid>> AccountsAsync(NidApp app, PersonId person)
        => (await BankingApi.AccountsAsync(app, person)).EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("id").GetGuid());

    internal static async Task PatchAsync(NidApp app, PersonId person, Guid account, object body)
        => Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(person, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{account}", body)).StatusCode);

    /// <summary>A booked line, filed by hand in <paramref name="category"/> when given.</summary>
    internal static async Task<Guid> LineAsync(NidApp app, Guid account, LocalDate on, decimal amount, string? category = null, string currency = "EUR")
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        var reference = $"ref-{Guid.NewGuid():N}";
        Assert.True(Currency.TryCreate(currency, out var money));
        await store.AddAsync(account, [new NewBankTransaction(reference, on, null, amount, money, $"CB MAGASIN {reference}", null, null, "api")], Ct);
        var id = (await store.ListAsync([account], null, null, 0, 500, Ct)).Single(t => t.ProviderRef == reference).Id;
        if (category is not null)
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ICategoryStore>()
                .SetForLinesAsync([id], Seed(category), CategorySource.Hand, "person:test", Instant.FromUtc(2025, 10, 15, 12, 0), true, Ct));
        return id;
    }

    private static async Task InternalAsync(NidApp app, Guid account, Guid line)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<ILinkStore>().SetAsync(
            new BankLink(line, null, null, null, account, LinkOutcome.InternalTransfer, null, "job", Instant.FromUtc(2025, 10, 15, 12, 0)),
            TransactionLinkState.Internal, Ct);
    }

    internal static async Task<JsonElement> SpendingAsync(NidApp app, PersonId person, string query = "")
    {
        var response = await app.RawAsync(person, HttpMethod.Get, $"{Spending}{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await BankingApi.JsonAsync(response);
    }

    private static JsonElement Category(JsonElement parent, string name)
        => parent.GetProperty(parent.TryGetProperty("categories", out _) ? "categories" : "children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == name);

    private static string[] Names(JsonElement list) => [.. list.EnumerateArray().Select(c => c.GetProperty("name").GetString()!)];

    private static string? Text(JsonElement element, string property)
        => element.GetProperty(property) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    [Fact]
    public async Task A_card_attached_to_no_account_with_lines_in_the_month_is_named_where_the_figures_leave_it_out()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Commun"), BankingApi.Account("uid-2", "hash-2", null, "Carte", "CARD"),
            BankingApi.Account("uid-3", "hash-3", null, "Vieille carte", "CARD"), BankingApi.Account("uid-4", "hash-4", null, "Perso"));
        var a = await AccountsAsync(app, app.AdultA);
        await PatchAsync(app, app.AdultA, a["Commun"], new { envelopeId = (await app.NewEnvelopeAsync("Commun", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000))).Value });
        await LineAsync(app, a["Carte"], new(2025, 10, 3), -40m);
        // A card with no line this month says nothing.
        await LineAsync(app, a["Vieille carte"], new(2025, 8, 3), -40m);

        static string[] Cards(JsonElement view) => [.. view.GetProperty("unattachedCards").EnumerateArray().Select(c => c.GetProperty("name").GetString()!)];
        async Task<string[]> DashboardAsync(PersonId person)
            => Cards(JsonDocument.Parse(await app.GetRawAsync(person, "/api/v1/dashboard?scope=household")).RootElement);
        // « Foyer » leaves a card on no account out; Bob cannot attach Alice's card.
        Assert.Equal(["Carte"], Cards(await SpendingAsync(app, app.AdultA)));
        Assert.Equal(["Carte"], await DashboardAsync(app.AdultA));
        Assert.Empty(Cards(await SpendingAsync(app, app.AdultB)));
        Assert.Empty(Cards(await SpendingAsync(app, app.AdultA, "?month=2025-09")));
        // « Moi » counts it already.
        Assert.Empty(Cards(await SpendingAsync(app, app.AdultA, "?scope=me")));

        await PatchAsync(app, app.AdultA, a["Carte"], new { cardOfAccountId = a["Commun"] });
        Assert.Empty(Cards(await SpendingAsync(app, app.AdultA)));
        Assert.Empty(await DashboardAsync(app.AdultA));
    }

    [Fact]
    public async Task A_month_nets_refunds_in_expense_categories_counts_uncategorised_debits_and_follows_the_scope()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Commun"), BankingApi.Account("uid-2", "hash-2", null, "Carte commune", "CARD"),
            BankingApi.Account("uid-3", "hash-3", null, "Perso A"));
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-b", "hash-b", null, "Perso B"));
        var a = await AccountsAsync(app, app.AdultA);
        var b = await AccountsAsync(app, app.AdultB);
        await PatchAsync(app, app.AdultA, a["Commun"], new { envelopeId = (await app.NewEnvelopeAsync("Commun", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000))).Value });
        await PatchAsync(app, app.AdultA, a["Carte commune"], new { cardOfAccountId = a["Commun"] });
        await PatchAsync(app, app.AdultA, a["Perso A"], new { envelopeId = (await app.NewEnvelopeAsync("Perso A", app.AdultA, Visibility.Private)).Value });
        await PatchAsync(app, app.AdultB, b["Perso B"], new { envelopeId = (await app.NewEnvelopeAsync("Perso B", app.AdultB, Visibility.Private)).Value });

        var joint = a["Commun"];
        await LineAsync(app, joint, new(2025, 10, 3), -80m, "food.groceries");
        // A refund filed in « Courses » lowers it; one filed in « Revenus › Remboursements » changes nothing.
        await LineAsync(app, joint, new(2025, 10, 5), 20m, "food.groceries");
        await LineAsync(app, joint, new(2025, 10, 8), 50m, "income.refunds");
        // A line on the parent counts in the parent only.
        await LineAsync(app, joint, new(2025, 10, 6), -30m, "food");
        // The card's line counts with its checking account.
        await LineAsync(app, a["Carte commune"], new(2025, 10, 7), -40m, "food.restaurants");
        await LineAsync(app, joint, new(2025, 10, 9), -500m, "internal.transfer");
        // Internal lines (a transfer, a card settlement) never count, uncategorised or filed by hand in an expense category.
        await InternalAsync(app, joint, await LineAsync(app, joint, new(2025, 10, 13), -300m));
        await InternalAsync(app, joint, await LineAsync(app, joint, new(2025, 10, 14), -200m, "food.groceries"));
        await LineAsync(app, joint, new(2025, 10, 10), -25m);
        await LineAsync(app, joint, new(2025, 10, 11), 15m);
        var ignored = await LineAsync(app, joint, new(2025, 10, 12), -70m, "food.groceries");
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/transactions/{ignored}/ignore")).StatusCode);
        // Another month: not this one's, and one month of history gives no average.
        await LineAsync(app, joint, new(2025, 9, 15), -45m, "food.groceries");
        await LineAsync(app, a["Perso A"], new(2025, 10, 4), -10m, "transport.fuel");
        await LineAsync(app, b["Perso B"], new(2025, 10, 4), -1000m, "food.groceries");

        foreach (var adult in new[] { app.AdultA, app.AdultB })
        {
            var household = await SpendingAsync(app, adult);
            Assert.Equal("EUR", household.GetProperty("currency").GetString());
            Assert.Equal("155.00", household.GetProperty("total").GetString());
            Assert.False(household.GetProperty("incomplete").GetBoolean());
            Assert.Null(Text(household, "average"));
            Assert.Equal(("2025-10", "2025-10"), (household.GetProperty("from").GetString(), household.GetProperty("to").GetString()));
            Assert.Equal(["Alimentation"], Names(household.GetProperty("categories")));
            var food = Category(household, "Alimentation");
            Assert.Equal("130.00", food.GetProperty("amount").GetString());
            Assert.Null(Text(food, "average"));
            Assert.Equal(["Courses", "Restaurants & cafés"], Names(food.GetProperty("children")));
            Assert.Equal("60.00", Category(food, "Courses").GetProperty("amount").GetString());
            Assert.Equal("40.00", Category(food, "Restaurants & cafés").GetProperty("amount").GetString());
            // #426: one debit to classify; the credit, the internal and the ignored lines are not.
            Assert.Equal(("25.00", 1), (household.GetProperty("uncategorised").GetProperty("amount").GetString(),
                household.GetProperty("uncategorised").GetProperty("count").GetInt32()));
        }

        // Bob's private line never counts for Alice; it counts in his own « Moi ».
        var mine = await SpendingAsync(app, app.AdultA, "?scope=me");
        Assert.Equal("165.00", mine.GetProperty("total").GetString());
        Assert.Equal(["Alimentation", "Transport"], Names(mine.GetProperty("categories")));
        Assert.Equal("10.00", Category(Category(mine, "Transport"), "Carburant").GetProperty("amount").GetString());
        var bob = await SpendingAsync(app, app.AdultB, "?scope=me");
        Assert.Equal("1155.00", bob.GetProperty("total").GetString());
        Assert.Equal("1060.00", Category(Category(bob, "Alimentation"), "Courses").GetProperty("amount").GetString());

        // The account filter offers the scope's accounts and counts only the picked ones; an account outside the scope counts nothing.
        Assert.Equal(["Carte commune", "Commun", "Perso A"], Names(mine.GetProperty("accounts")));
        var picked = await SpendingAsync(app, app.AdultA, $"?scope=me&account={a["Perso A"]}&account={a["Carte commune"]}");
        Assert.Equal("50.00", picked.GetProperty("total").GetString());
        Assert.Equal(["Carte commune", "Commun", "Perso A"], Names(picked.GetProperty("accounts")));
        Assert.Null(Text(picked, "committed"));
        Assert.Equal("0.00", (await SpendingAsync(app, app.AdultA, $"?scope=me&account={b["Perso B"]}")).GetProperty("total").GetString());
        var lines = await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get,
            $"{Spending}/lines?scope=me&categoryId={Seed("food")}&account={a["Carte commune"]}"));
        Assert.Equal(("40.00", 1), (lines.GetProperty("total").GetString(), lines.GetProperty("count").GetInt32()));
    }

    [Fact]
    public async Task A_foreign_line_converts_at_its_month_s_rate_and_without_one_the_month_is_incomplete()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Compte", currency: "USD"));
        var account = (await AccountsAsync(app, app.AdultA))["Compte"];
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IFxRateStore>().WriteAsync([
                new FxRate(new LocalDate(2025, 9, 30), Currency.Eur, 0.9m, FxRateSource.Ecb),
                new FxRate(new LocalDate(2025, 10, 1), Currency.Eur, 0.5m, FxRateSource.Ecb),
                new FxRate(new LocalDate(2025, 10, 15), Currency.Eur, 0.8m, FxRateSource.Ecb),
            ], Ct);
        await LineAsync(app, account, new(2025, 9, 10), -100m, "food.groceries", "USD");
        await LineAsync(app, account, new(2025, 10, 5), -50m, "food.groceries", "USD");
        await LineAsync(app, account, new(2025, 7, 1), -20m, "food.groceries");
        await LineAsync(app, account, new(2025, 8, 10), -20m, "food.groceries");
        await LineAsync(app, account, new(2025, 8, 12), -100m, "food.groceries", "GBP");

        // A past month at its month-end rate.
        var september = await SpendingAsync(app, app.AdultA, "?scope=me&month=2025-09");
        Assert.Equal("90.00", september.GetProperty("total").GetString());
        // August's GBP line has no rate, but it is only in the average's window.
        Assert.False(september.GetProperty("incomplete").GetBoolean());
        // The current month at today's rate. July to September cover the average's window, but August's GBP line has no rate: no
        // average rather than a short one.
        var october = await SpendingAsync(app, app.AdultA, "?scope=me");
        Assert.Equal("40.00", october.GetProperty("total").GetString());
        Assert.False(october.GetProperty("incomplete").GetBoolean());
        Assert.Null(Text(october, "average"));
        Assert.Null(Text(Category(october, "Alimentation"), "average"));
        Assert.Null(Text(october.GetProperty("uncategorised"), "average"));
        // No GBP rate: the line is left out, never guessed, and the figure flagged.
        var august = await SpendingAsync(app, app.AdultA, "?scope=me&month=2025-08");
        Assert.Equal("20.00", august.GetProperty("total").GetString());
        Assert.True(august.GetProperty("incomplete").GetBoolean());
        // A period converts each month at its own rate.
        var period = await SpendingAsync(app, app.AdultA, "?scope=me&from=2025-09&to=2025-10");
        Assert.Equal("130.00", period.GetProperty("total").GetString());
        Assert.Equal([("2025-09", "90.00"), ("2025-10", "40.00")], Months(period));
        AssertMonthsAddUp(period);
        Assert.False(period.GetProperty("incomplete").GetBoolean());
    }

    [Fact]
    public async Task The_average_needs_three_months_of_history_and_a_period_is_24_months_at_most()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Compte"));
        var account = (await AccountsAsync(app, app.AdultA))["Compte"];
        // A first line on the 1st: July is a whole month.
        await LineAsync(app, account, new(2025, 7, 1), -120m, "food.groceries");
        await LineAsync(app, account, new(2025, 8, 10), -60m, "food.groceries");
        await LineAsync(app, account, new(2025, 8, 20), -24m, "transport.fuel");
        await LineAsync(app, account, new(2025, 9, 10), -180m, "food.groceries");
        await LineAsync(app, account, new(2025, 10, 2), -10m, "food.groceries");

        // July to September: three months of the twelve before October, and the average is theirs (384 over 3), not over 12.
        var october = await SpendingAsync(app, app.AdultA, "?scope=me");
        Assert.Equal("10.00", october.GetProperty("total").GetString());
        Assert.Equal("128.00", october.GetProperty("average").GetString());
        Assert.Equal("120.00", Category(Category(october, "Alimentation"), "Courses").GetProperty("average").GetString());
        // No spending this month but an average: still listed.
        var transport = Category(october, "Transport");
        Assert.Equal(("0.00", "8.00"), (transport.GetProperty("amount").GetString(), transport.GetProperty("average").GetString()));
        Assert.Equal(["Alimentation", "Transport"], Names(october.GetProperty("categories")));
        Assert.Equal("0.00", october.GetProperty("uncategorised").GetProperty("average").GetString());
        // Two months of history before September: no average.
        var september = await SpendingAsync(app, app.AdultA, "?scope=me&month=2025-09");
        Assert.Null(Text(september, "average"));
        Assert.Null(Text(Category(september, "Alimentation"), "average"));

        var period = await SpendingAsync(app, app.AdultA, "?scope=me&from=2025-08&to=2025-10");
        Assert.Equal(("274.00", "2025-08", "2025-10"),
            (period.GetProperty("total").GetString(), period.GetProperty("from").GetString(), period.GetProperty("to").GetString()));
        // Each month of the range, a month with nothing included, under the total's rules: they add up to it.
        Assert.Equal([("2025-08", "84.00"), ("2025-09", "180.00"), ("2025-10", "10.00")], Months(period));
        AssertMonthsAddUp(period);
        Assert.Equal([("2025-09", "180.00")], Months(september));
        Assert.Equal("2023-11", (await SpendingAsync(app, app.AdultA, "?from=2023-11&to=2025-10")).GetProperty("from").GetString());
        foreach (var query in new[] { "?from=2023-10&to=2025-10", "?from=2025-10&to=2025-09", "?from=2025-01", "?month=2025-10&from=2025-01&to=2025-02" })
            await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Spending}{query}"),
                HttpStatusCode.UnprocessableEntity, BankingCodes.SpendingPeriod);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Spending}?month=2025-13")).StatusCode);
    }

    [Fact]
    public async Task A_first_line_mid_month_leaves_that_month_out_of_the_average()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Compte"));
        var account = (await AccountsAsync(app, app.AdultA))["Compte"];
        // June is only half there: neither a covered month nor in the sum.
        await LineAsync(app, account, new(2025, 6, 15), -900m, "food.groceries");
        await LineAsync(app, account, new(2025, 7, 10), -30m, "food.groceries");
        await LineAsync(app, account, new(2025, 8, 10), -30m, "food.groceries");
        await LineAsync(app, account, new(2025, 9, 10), -60m, "food.groceries");

        Assert.Equal("40.00", (await SpendingAsync(app, app.AdultA, "?scope=me")).GetProperty("average").GetString());
        Assert.Null(Text(await SpendingAsync(app, app.AdultA, "?scope=me&month=2025-09"), "average"));
    }

    [Fact]
    public async Task The_average_starts_no_earlier_than_the_retention_keeps_lines()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Compte"));
        var account = (await AccountsAsync(app, app.AdultA))["Compte"];
        // A line the purge kept (a linked one) from long before the retention: it does not make the history longer.
        await LineAsync(app, account, new(2025, 1, 1), -900m, "food.groceries");
        await LineAsync(app, account, new(2025, 7, 10), -30m, "food.groceries");
        await LineAsync(app, account, new(2025, 8, 10), -30m, "food.groceries");
        await LineAsync(app, account, new(2025, 9, 10), -60m, "food.groceries");

        // No retention: January to September, 1020 over 9.
        Assert.Equal("113.33", (await SpendingAsync(app, app.AdultA, "?scope=me")).GetProperty("average").GetString());
        // Kept from 2025-06-15: July is the first whole month, 120 over 3.
        await RetentionAsync(app, 4);
        Assert.Equal("40.00", (await SpendingAsync(app, app.AdultA, "?scope=me")).GetProperty("average").GetString());
        // Kept from 2025-07-15: August and September only.
        await RetentionAsync(app, 3);
        Assert.Null(Text(await SpendingAsync(app, app.AdultA, "?scope=me"), "average"));
    }

    private static async Task RetentionAsync(NidApp app, int months)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        Assert.True((await scope.ServiceProvider.GetRequiredService<IBankingSettings>()
            .UpdateAsync(new BankingSettingsChange(BankRetentionMonths: months), Ct)).IsSuccess);
    }

    private static (string, string)[] Months(JsonElement view)
        => [.. view.GetProperty("months").EnumerateArray().Select(m => (m.GetProperty("month").GetString()!, m.GetProperty("total").GetString()!))];

    private static void AssertMonthsAddUp(JsonElement view)
        => Assert.Equal(decimal.Parse(view.GetProperty("total").GetString()!, CultureInfo.InvariantCulture),
            view.GetProperty("months").EnumerateArray().Sum(m => decimal.Parse(m.GetProperty("total").GetString()!, CultureInfo.InvariantCulture)));

    private static async Task<JsonElement> LinesAsync(NidApp app, PersonId person, string query)
    {
        var response = await app.RawAsync(person, HttpMethod.Get, $"{Spending}/lines{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await BankingApi.JsonAsync(response);
    }

    private static string[] Amounts(JsonElement lines) => [.. lines.GetProperty("items").EnumerateArray().Select(l => l.GetProperty("amount").GetString()!).Order()];

    [Fact]
    public async Task A_category_s_lines_are_the_ones_its_figure_sums_and_page_by_fifty()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Commun"), BankingApi.Account("uid-2", "hash-2", null, "Carte commune", "CARD"));
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-b", "hash-b", null, "Perso B"));
        var a = await AccountsAsync(app, app.AdultA);
        var b = await AccountsAsync(app, app.AdultB);
        await PatchAsync(app, app.AdultA, a["Commun"], new { envelopeId = (await app.NewEnvelopeAsync("Commun", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000))).Value });
        await PatchAsync(app, app.AdultA, a["Carte commune"], new { cardOfAccountId = a["Commun"] });
        await PatchAsync(app, app.AdultB, b["Perso B"], new { envelopeId = (await app.NewEnvelopeAsync("Perso B", app.AdultB, Visibility.Private)).Value });

        var joint = a["Commun"];
        await LineAsync(app, joint, new(2025, 10, 3), -80m, "food.groceries");
        await LineAsync(app, joint, new(2025, 10, 5), 20m, "food.groceries");
        await LineAsync(app, joint, new(2025, 10, 8), 50m, "income.refunds");
        await LineAsync(app, joint, new(2025, 10, 6), -30m, "food");
        await LineAsync(app, a["Carte commune"], new(2025, 10, 7), -40m, "food.restaurants");
        await InternalAsync(app, joint, await LineAsync(app, joint, new(2025, 10, 13), -300m));
        await InternalAsync(app, joint, await LineAsync(app, joint, new(2025, 10, 14), -200m, "food.groceries"));
        await LineAsync(app, joint, new(2025, 10, 10), -25m);
        await LineAsync(app, joint, new(2025, 10, 11), 15m);
        await LineAsync(app, joint, new(2025, 9, 15), -45m, "food.groceries");
        await LineAsync(app, b["Perso B"], new(2025, 10, 4), -1000m, "food.groceries");
        for (var i = 0; i < 55; i++)
            await LineAsync(app, joint, new(2025, 10, 1), -1m, "transport.fuel");

        var figures = await SpendingAsync(app, app.AdultA);
        var food = Category(figures, "Alimentation");
        var foodId = food.GetProperty("id").GetGuid();
        // A parent: its own lines, its children's and the refund; no transfer, no income, no other month, nothing of Bob's.
        var lines = await LinesAsync(app, app.AdultA, $"?categoryId={foodId}");
        Assert.Equal(["-30.00", "-40.00", "-80.00", "20.00"], Amounts(lines));
        Assert.Equal((food.GetProperty("amount").GetString(), 4, false), (lines.GetProperty("total").GetString(), lines.GetProperty("count").GetInt32(), lines.GetProperty("incomplete").GetBoolean()));
        var groceries = Category(food, "Courses");
        var child = await LinesAsync(app, app.AdultA, $"?categoryId={groceries.GetProperty("id").GetGuid()}&month=2025-10");
        Assert.Equal(["-80.00", "20.00"], Amounts(child));
        Assert.Equal(groceries.GetProperty("amount").GetString(), child.GetProperty("total").GetString());
        // A period lists its months' lines and sums them as the figures do.
        var period = await LinesAsync(app, app.AdultA, $"?categoryId={foodId}&from=2025-09&to=2025-10");
        Assert.Equal(5, period.GetProperty("count").GetInt32());
        Assert.Equal(Category(await SpendingAsync(app, app.AdultA, "?from=2025-09&to=2025-10"), "Alimentation").GetProperty("amount").GetString(), period.GetProperty("total").GetString());
        // « Non classées »: the uncategorised debits only.
        var none = await LinesAsync(app, app.AdultA, "?uncategorised=true");
        Assert.Equal(["-25.00"], Amounts(none));
        Assert.Equal(figures.GetProperty("uncategorised").GetProperty("amount").GetString(), none.GetProperty("total").GetString());
        Assert.Equal(none.GetProperty("count").GetInt32(), figures.GetProperty("uncategorised").GetProperty("count").GetInt32());
        // Bob's account is not Alice's to read: his category lines are his alone.
        var bob = await LinesAsync(app, app.AdultB, $"?categoryId={foodId}&scope=me");
        Assert.Equal(["-1000.00", "-30.00", "-40.00", "-80.00", "20.00"], Amounts(bob));

        var fuelId = Category(figures, "Transport").GetProperty("id").GetGuid();
        var first = await LinesAsync(app, app.AdultA, $"?categoryId={fuelId}");
        var second = await LinesAsync(app, app.AdultA, $"?categoryId={fuelId}&page=2");
        Assert.Equal((50, 5, 55, "55.00"), (first.GetProperty("items").GetArrayLength(), second.GetProperty("items").GetArrayLength(), first.GetProperty("count").GetInt32(), first.GetProperty("total").GetString()));
        Assert.Empty(first.GetProperty("items").EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).Intersect(second.GetProperty("items").EnumerateArray().Select(l => l.GetProperty("id").GetGuid())));

        foreach (var query in new[] { "", $"?categoryId={foodId}&uncategorised=true" })
            await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Spending}/lines{query}"), HttpStatusCode.UnprocessableEntity, BankingCodes.SpendingTarget);
    }

    [Fact]
    public async Task A_line_with_no_rate_flags_the_lines_total_as_the_figures_do()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Compte"));
        var account = (await AccountsAsync(app, app.AdultA))["Compte"];
        await LineAsync(app, account, new(2025, 8, 10), -20m, "food.groceries");
        await LineAsync(app, account, new(2025, 8, 12), -100m, "food.groceries", "GBP");
        await LineAsync(app, account, new(2025, 8, 14), -15m, "transport.fuel");

        var figures = await SpendingAsync(app, app.AdultA, "?scope=me&month=2025-08");
        Assert.True(figures.GetProperty("incomplete").GetBoolean());
        var id = Category(figures, "Alimentation").GetProperty("id").GetGuid();
        var lines = await LinesAsync(app, app.AdultA, $"?scope=me&month=2025-08&categoryId={id}");

        Assert.Equal(2, lines.GetProperty("count").GetInt32());
        Assert.Equal(("20.00", true), (lines.GetProperty("total").GetString(), lines.GetProperty("incomplete").GetBoolean()));
        // Another category's lines all have their rate: their total is whole.
        var fuel = await LinesAsync(app, app.AdultA, $"?scope=me&month=2025-08&categoryId={Seed("transport")}");
        Assert.Equal(("15.00", false), (fuel.GetProperty("total").GetString(), fuel.GetProperty("incomplete").GetBoolean()));
    }

    [Fact]
    public async Task A_child_under_a_parent_that_nets_to_zero_keeps_its_lines_total()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Compte"));
        var account = (await AccountsAsync(app, app.AdultA))["Compte"];
        await LineAsync(app, account, new(2025, 10, 3), -30m, "food.groceries");
        await LineAsync(app, account, new(2025, 10, 4), 30m, "food");

        // « Alimentation » nets to nothing, so the view hides it and its child; the child's lines still total its 30.
        Assert.Empty((await SpendingAsync(app, app.AdultA, "?scope=me")).GetProperty("categories").EnumerateArray());
        var lines = await LinesAsync(app, app.AdultA, $"?scope=me&categoryId={Seed("food.groceries")}");
        Assert.Equal(("30.00", 1), (lines.GetProperty("total").GetString(), lines.GetProperty("count").GetInt32()));
        Assert.Equal("0.00", (await LinesAsync(app, app.AdultA, $"?scope=me&categoryId={Seed("food")}")).GetProperty("total").GetString());
    }
}
