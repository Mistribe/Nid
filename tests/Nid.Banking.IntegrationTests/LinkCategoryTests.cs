using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Application.Reconciliation;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Banking.IntegrationTests;

/// <summary>Transaction categories spec §Category from the link: a linked line takes its link's category, below a person's choice.</summary>
public class LinkCategoryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LocalDate Today = new(2026, 10, 29);
    private static readonly Instant At = Today.At(new LocalTime(10, 0)).InUtc().ToInstant();

    private sealed class FixedClock : IClock
    {
        public Instant GetCurrentInstant() => At;
    }

    private static LocalDate D(int month, int day) => new(2026, month, day);

    private static Guid Seed(string key) => CategorySeed.Find(key)!.Id;

    private static Task<NidApp> AppAsync(PostgresFixture fixture)
        => BankingApi.StartAsync(fixture, overrides: s => s.AddSingleton<IClock>(new FixedClock()));

    /// <summary>A's « Courant A », and any more of A's accounts.</summary>
    private static async Task<(NidApp App, Guid Account)> StartAsync(PostgresFixture fixture, params SessionAccount[] more)
    {
        var app = await AppAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", [BankingApi.Account("uid-1", "hash-1", null, "Courant A"), .. more]);
        return (app, await AccountAsync(app, "Courant A"));
    }

    private static async Task<Guid> AccountAsync(NidApp app, string name)
        => (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray().Single(a => a.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

    private static async Task<Guid> AddAsync(NidApp app, Guid account, string reference, LocalDate on, decimal amount, string label = "PRLV SEPA NETFLIX 123")
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        await store.AddAsync(account, [new NewBankTransaction(reference, on, null, amount, Currency.Eur, label, null, null, "api")], default);
        return (await store.ListAsync([account], null, null, 0, 100, default)).Single(t => t.ProviderRef == reference).Id;
    }

    /// <summary>A's subscription of 12.99 a month from 2026-01-05.</summary>
    private static async Task<ItemId> MonthlyAsync(NidApp app)
    {
        var id = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2026-01-05",
            price = new { amount = "12.99", currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        return id;
    }

    /// <summary>A's property rented 750 + 50 of charges a month since 2025-01-01.</summary>
    private static async Task<Guid> RentedAsync(NidApp app)
    {
        var id = await PropertyApi.NewPropertyAsync(app, PropertyApi.Home(app, visibility: "private"));
        await LoanApi.CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{PropertyApi.Properties}/{id}/tenancies",
            new { label = "Bail", startsOn = "2025-01-01", rentMonthly = "750", chargesMonthly = "50" }));
        return id;
    }

    private static Task<HttpResponseMessage> PostAsync(NidApp app, Guid line, string action, object? body = null)
        => app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/transactions/{line}/{action}", body ?? new { });

    private static async Task LinkAsync(NidApp app, Guid line, string module, Guid item)
    {
        var response = await PostAsync(app, line, "link", new { module, itemId = item });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task OkAsync(NidApp app, Guid line, string action)
    {
        var response = await PostAsync(app, line, action);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<BankTransaction> LineAsync(NidApp app, Guid line)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return (await scope.ServiceProvider.GetRequiredService<ITransactionStore>().FindAsync(line, default))!;
    }

    private static async Task<(Guid? Category, CategorySource? Source)> CategoryAsync(NidApp app, Guid line)
        => await LineAsync(app, line) is var l ? (l.Category, l.CategorySource) : default;

    private static async Task CategoriseAsync(NidApp app, Guid line, string key, CategorySource source, bool byPerson)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ICategoryStore>()
            .SetForLinesAsync([line], Seed(key), source, $"person:{app.AdultA.Value:D}", At, byPerson, default));
    }

    [Fact]
    public async Task Linking_a_line_to_a_subscription_sets_its_category_from_the_provider_label()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var line = await AddAsync(app, account, "a", D(10, 5), -12.99m);
        var box = await MonthlyAsync(app);
        await using (var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
        {
            // The first label that maps wins: « other » maps to nothing.
            subs.Providers.Add(new ProviderRow
            {
                Key = "box-alpha", Origin = "catalog", Name = "Box Alpha", CatalogCommit = CatalogFixtures.PinnedCommit, LabelKeys = ["other", "internet", "streaming"],
            });
            await subs.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await app.LinkProviderAsync(box, "box-alpha");

        await LinkAsync(app, line, "subscriptions", box.Value);

        Assert.Equal((Seed("home.telecom"), CategorySource.Link), await CategoryAsync(app, line));
    }

    [Fact]
    public async Task A_subscription_without_a_provider_gets_subscriptions()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var line = await AddAsync(app, account, "a", D(10, 5), -12.99m);

        await LinkAsync(app, line, "subscriptions", (await MonthlyAsync(app)).Value);

        Assert.Equal((Seed("leisure.subscriptions"), CategorySource.Link), await CategoryAsync(app, line));
    }

    [Fact]
    public async Task An_internal_transfer_pair_gets_internal_transfer_on_both_lines()
    {
        var (app, courant) = await StartAsync(fixture, BankingApi.Account("uid-2", "hash-2", null, "Livret A", "SVGS"));
        await using var _ = app;
        var debit = await AddAsync(app, courant, "a", D(10, 5), -500m, "VIR VERS LIVRET");
        var credit = await AddAsync(app, await AccountAsync(app, "Livret A"), "b", D(10, 6), 500m, "VIR DE COURANT");

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            Assert.Equal(2, (await scope.ServiceProvider.GetRequiredService<BankReconciliation>().RunAsync(null, Today, TestContext.Current.CancellationToken)).Internal);
        }

        Assert.Equal((Seed("internal.transfer"), CategorySource.Link), await CategoryAsync(app, debit));
        Assert.Equal((Seed("internal.transfer"), CategorySource.Link), await CategoryAsync(app, credit));
    }

    [Fact]
    public async Task A_hand_category_survives_a_later_link()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var line = await AddAsync(app, account, "a", D(10, 5), -12.99m);
        await CategoriseAsync(app, line, "food.groceries", CategorySource.Hand, byPerson: true);

        await LinkAsync(app, line, "subscriptions", (await MonthlyAsync(app)).Value);

        Assert.Equal((Seed("food.groceries"), CategorySource.Hand), await CategoryAsync(app, line));
    }

    [Fact]
    public async Task Unlinking_clears_a_link_category_and_keeps_a_hand_one()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var line = await AddAsync(app, account, "a", D(10, 5), -12.99m);
        var older = await AddAsync(app, account, "b", D(9, 5), -12.99m);
        await CategoriseAsync(app, older, "food.groceries", CategorySource.Hand, byPerson: true);

        await LinkAsync(app, line, "subscriptions", (await MonthlyAsync(app)).Value); // the new rule links the older line too
        Assert.Equal(TransactionLinkState.Linked, (await LineAsync(app, older)).LinkState);
        Assert.Equal((Seed("food.groceries"), CategorySource.Hand), await CategoryAsync(app, older));
        await OkAsync(app, line, "unlink");
        await OkAsync(app, older, "unlink");

        Assert.Equal((null, null), await CategoryAsync(app, line));
        Assert.Equal((Seed("food.groceries"), CategorySource.Hand), await CategoryAsync(app, older));
    }

    [Fact]
    public async Task Ignoring_clears_a_non_hand_category()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var hinted = await AddAsync(app, account, "a", D(10, 5), -30m, "CARREFOUR CITY");
        var chosen = await AddAsync(app, account, "b", D(10, 6), -30m, "CARREFOUR CITY");
        await CategoriseAsync(app, hinted, "food.groceries", CategorySource.Hint, byPerson: false);
        await CategoriseAsync(app, chosen, "food.groceries", CategorySource.Hand, byPerson: true);

        await OkAsync(app, hinted, "ignore");
        await OkAsync(app, chosen, "ignore");

        Assert.Equal((null, null), await CategoryAsync(app, hinted));
        Assert.Equal((Seed("food.groceries"), CategorySource.Hand), await CategoryAsync(app, chosen));
    }

    [Fact]
    public async Task Deleting_the_linked_subscription_clears_the_link_category()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var line = await AddAsync(app, account, "a", D(10, 5), -12.99m);
        var older = await AddAsync(app, account, "b", D(9, 5), -12.99m);
        await CategoriseAsync(app, older, "food.groceries", CategorySource.Hand, byPerson: true);
        var netflix = await MonthlyAsync(app);
        await LinkAsync(app, line, "subscriptions", netflix.Value); // the new rule links the older line too

        var url = $"/api/v1/subscriptions/{netflix.Value}";
        var version = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{url}/deletion")).RootElement.GetProperty("version").GetUInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{url}?version={version}")).StatusCode);

        Assert.Equal((null, null), await CategoryAsync(app, line));
        Assert.Equal((Seed("food.groceries"), CategorySource.Hand), await CategoryAsync(app, older));
    }

    [Fact]
    public async Task A_rent_cash_in_gets_income_rent()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var property = await RentedAsync(app);
        var line = await AddAsync(app, account, "a", D(10, 5), 800m, "VIR SEPA DUPONT LOYER");

        await LinkAsync(app, line, "realestate", property);

        Assert.Equal((Seed("income.rent"), CategorySource.Link), await CategoryAsync(app, line));
    }
}
