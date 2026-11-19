using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Application.Reconciliation;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Banking.IntegrationTests;

/// <summary>Transaction categories spec §Rules: the reconciliation pass applies the household's category rules, below a link or a person's choice.</summary>
public class CategoryRuleReconciliationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LocalDate Today = new(2026, 10, 29);
    private static readonly Instant At = Today.At(new LocalTime(10, 0)).InUtc().ToInstant();

    private sealed class FixedClock : IClock
    {
        public Instant GetCurrentInstant() => At;
    }

    private static LocalDate D(int month, int day) => new(2026, month, day);

    private static Guid Seed(string key) => CategorySeed.Find(key)!.Id;

    private static BankingDbContext Db(NidApp app) => new(new DbContextOptionsBuilder<BankingDbContext>().Configure(app.ConnectionString).Options);

    /// <summary>A's « Courant A », private to A.</summary>
    private static async Task<(NidApp App, Guid Account)> StartAsync(PostgresFixture fixture)
    {
        var app = await BankingApi.StartAsync(fixture, overrides: s => s.AddSingleton<IClock>(new FixedClock()));
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        var account = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray()
            .Single(a => a.GetProperty("name").GetString() == "Courant A").GetProperty("id").GetGuid();
        return (app, account);
    }

    private static async Task<T> InScopeAsync<T>(NidApp app, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return await work(scope.ServiceProvider);
    }

    private static Task<Guid> AddAsync(
        NidApp app, Guid account, string reference, decimal amount, string label = "PRLV SEPA NETFLIX 123", string? counterparty = null)
        => InScopeAsync(app, async sp =>
        {
            var store = sp.GetRequiredService<ITransactionStore>();
            await store.AddAsync(account, [new NewBankTransaction(reference, D(10, 5), null, amount, Currency.Eur, label, counterparty, null, "api")], default);
            return (await store.ListAsync([account], null, null, 0, 100, default)).Single(t => t.ProviderRef == reference).Id;
        });

    private static Task<CategoryRule> RuleAsync(NidApp app, string pattern, string key, PersonId by, Guid? account = null)
        => RuleAsync(app, pattern, Seed(key), by, account);

    private static Task<CategoryRule> RuleAsync(NidApp app, string pattern, Guid category, PersonId by, Guid? account = null)
        => InScopeAsync(app, sp => sp.GetRequiredService<ICategoryRuleStore>().CreateAsync(
            new CategoryRule(Guid.Empty, pattern, account, null, null, null, category, by, default, null), default));

    private static Task<CategoryRule?> FindRuleAsync(NidApp app, Guid rule)
        => InScopeAsync(app, sp => sp.GetRequiredService<ICategoryRuleStore>().FindAsync(rule, default));

    private static Task<ReconciliationCounts> PassAsync(NidApp app)
        => InScopeAsync(app, sp => sp.GetRequiredService<BankReconciliation>().RunAsync(null, Today, default));

    private static async Task<(Guid? Category, string? Source, Guid? Rule, string? By)> CategoryAsync(NidApp app, Guid line)
    {
        await using var db = Db(app);
        var row = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == line);
        return (row.CategoryId, row.CategorySource, row.CategoryRuleId, row.CategorisedBy);
    }

    private static Task<int> CategoriseAsync(NidApp app, Guid line, string key, CategorySource source, bool byPerson)
        => InScopeAsync(app, sp => sp.GetRequiredService<ICategoryStore>()
            .SetForLinesAsync([line], Seed(key), source, $"person:{app.AdultA.Value:D}", At, byPerson, default));

    private static async Task OkAsync(HttpResponseMessage response)
        => Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

    [Fact]
    public async Task A_new_synced_line_takes_the_matching_rules_category()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var netflix = await RuleAsync(app, "NETFLIX", "leisure.subscriptions", app.AdultA);
        var carrefour = await RuleAsync(app, "CARREFOUR CITY", "food.groceries", app.AdultA);
        var byLabel = await AddAsync(app, account, "a", -12.99m);
        // The label says nothing; the counterparty, normalised like a label, does.
        var byCounterparty = await AddAsync(app, account, "b", -23.40m, "CB 1234 PAIEMENT", "Carrefour City");
        var none = await AddAsync(app, account, "c", -5m, "CB BOULANGERIE");

        await PassAsync(app);

        Assert.Equal((Seed("leisure.subscriptions"), "rule", netflix.Id, $"rule:{netflix.Id:D}"), await CategoryAsync(app, byLabel));
        Assert.Equal((Seed("food.groceries"), "rule", carrefour.Id, $"rule:{carrefour.Id:D}"), await CategoryAsync(app, byCounterparty));
        Assert.Equal((null, null, null, null), await CategoryAsync(app, none));
        Assert.Equal(At, (await FindRuleAsync(app, netflix.Id))!.LastUsedAt);
    }

    [Fact]
    public async Task A_rule_never_touches_hand_assistant_or_link_lines()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var rule = await RuleAsync(app, "NETFLIX", "leisure.subscriptions", app.AdultA);
        var hand = await AddAsync(app, account, "a", -12.99m);
        var assistant = await AddAsync(app, account, "b", -12.99m);
        var link = await AddAsync(app, account, "c", -12.99m);
        await CategoriseAsync(app, hand, "home.telecom", CategorySource.Hand, byPerson: true);
        await CategoriseAsync(app, assistant, "home.telecom", CategorySource.Assistant, byPerson: true);
        await CategoriseAsync(app, link, "home.telecom", CategorySource.Link, byPerson: false);

        await PassAsync(app);

        Assert.Equal((Seed("home.telecom"), "hand"), await CategoryAsync(app, hand) is var h ? (h.Category, h.Source) : default);
        Assert.Equal((Seed("home.telecom"), "assistant"), await CategoryAsync(app, assistant) is var a ? (a.Category, a.Source) : default);
        Assert.Equal((Seed("home.telecom"), "link"), await CategoryAsync(app, link) is var l ? (l.Category, l.Source) : default);
        Assert.Null((await FindRuleAsync(app, rule.Id))!.LastUsedAt);
    }

    [Fact]
    public async Task A_rule_of_an_author_who_does_not_write_the_account_does_not_apply()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var bobs = await RuleAsync(app, "NETFLIX", "leisure.subscriptions", app.AdultB);
        var line = await AddAsync(app, account, "a", -12.99m);

        await PassAsync(app);

        Assert.Equal((null, null, null, null), await CategoryAsync(app, line));
        Assert.Null((await FindRuleAsync(app, bobs.Id))!.LastUsedAt);
    }

    [Fact]
    public async Task Unlinking_applies_the_rule_at_once()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var line = await AddAsync(app, account, "a", -12.99m);
        var box = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/transactions/{line}/link",
            new { module = "subscriptions", itemId = box.Value }));
        var rule = await RuleAsync(app, "NETFLIX", "home.telecom", app.AdultA);
        Assert.Equal("link", (await CategoryAsync(app, line)).Source);

        await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/transactions/{line}/unlink", new { }));

        Assert.Equal((Seed("home.telecom"), "rule", rule.Id, $"rule:{rule.Id:D}"), await CategoryAsync(app, line));
    }

    [Fact]
    public async Task Unlinking_applies_only_the_rules_of_authors_who_write_the_account()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var line = await AddAsync(app, account, "a", -12.99m);
        var box = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/transactions/{line}/link",
            new { module = "subscriptions", itemId = box.Value }));
        var bobs = await RuleAsync(app, "NETFLIX", "home.telecom", app.AdultB);

        await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/transactions/{line}/unlink", new { }));

        Assert.Equal((null, null, null, null), await CategoryAsync(app, line));
        Assert.Null((await FindRuleAsync(app, bobs.Id))!.LastUsedAt);
    }

    [Fact]
    public async Task A_rule_never_categorises_an_ignored_line()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var line = await AddAsync(app, account, "a", -12.99m);
        await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/transactions/{line}/ignore", new { }));
        await RuleAsync(app, "NETFLIX", "home.telecom", app.AdultA);

        await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{BankingApi.Root}/transactions/{line}/category"));

        Assert.Equal((null, null, null, null), await CategoryAsync(app, line));
    }

    [Fact]
    public async Task Leaving_it_to_Nid_applies_the_rule_at_once()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var line = await AddAsync(app, account, "a", -12.99m);
        await CategoriseAsync(app, line, "food.groceries", CategorySource.Hand, byPerson: true);
        var rule = await RuleAsync(app, "NETFLIX", "home.telecom", app.AdultA);

        await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{BankingApi.Root}/transactions/{line}/category"));

        Assert.Equal((Seed("home.telecom"), "rule", rule.Id, $"rule:{rule.Id:D}"), await CategoryAsync(app, line));
    }

    [Fact]
    public async Task Merging_a_rules_category_moves_the_rule()
    {
        var (app, account) = await StartAsync(fixture);
        await using var _ = app;
        var streaming = await InScopeAsync(app, async sp => (await sp.GetRequiredService<ICategoryStore>()
            .CreateAsync(Seed("leisure"), "Streaming", CategoryKind.Expense, default)).TryGetValue(out var created) ? created!.Id : throw new InvalidOperationException());
        var rule = await RuleAsync(app, "NETFLIX", streaming, app.AdultA);

        await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/categories/{streaming}/merge",
            new { into = Seed("leisure.subscriptions") }));

        Assert.Equal(Seed("leisure.subscriptions"), (await FindRuleAsync(app, rule.Id))!.Category);
        var line = await AddAsync(app, account, "a", -12.99m);
        await PassAsync(app);
        Assert.Equal((Seed("leisure.subscriptions"), "rule"), await CategoryAsync(app, line) is var c ? (c.Category, c.Source) : default);
    }
}
