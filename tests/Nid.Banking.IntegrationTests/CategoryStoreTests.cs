using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;
using Duration = NodaTime.Duration;

namespace Nid.Banking.IntegrationTests;

/// <summary>Transaction categories spec §Model: the seed, the tree's rules, merges and the precedence of a line's category.</summary>
public class CategoryStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LocalDate Day = new(2026, 10, 12);
    private static readonly Instant At = NidApp.GoldenCaseToday.AtMidnight().InUtc().ToInstant();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid Seed(string key) => CategorySeed.Find(key)!.Id;

    private static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    private static async Task<List<Guid>> LinesAsync(AsyncServiceScope scope, NidApp app, params string[] refs)
    {
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var id = await connections.CreatePendingAsync(app.AdultA, "Boursorama", "FR", [1, 2, 3], At + Duration.FromMinutes(15), Ct);
        await connections.ActivateAsync(id, "session", At + Duration.FromDays(180), Ct);
        var account = (await scope.ServiceProvider.GetRequiredService<IAccountStore>()
            .UpsertFromSessionAsync(id, [new SessionAccount("uid", "hash", "FR7630006000011234567890189", "Compte", "CACC", "EUR")], Ct))[0].Id;
        var transactions = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        await transactions.AddAsync(account, [.. refs.Select((r, i) => new NewBankTransaction(r, Day, null, -10m - i, Currency.Eur, "CARREFOUR CITY " + r, null, null, "api"))], Ct);
        return [.. (await transactions.ListAsync([account], null, null, 0, 10, Ct)).OrderBy(t => t.ProviderRef).Select(t => t.Id)];
    }

    private static string? Code<T>(Result<T> result) where T : notnull => result.TryGetFailure(out var failure) ? failure!.Code : null;

    private static T Value<T>(Result<T> result) where T : notnull => result.TryGetValue(out var value) ? value : throw new InvalidOperationException(Code(result));

    [Fact]
    public async Task Seed_is_in_the_database()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var all = await scope.ServiceProvider.GetRequiredService<ICategoryStore>().AllAsync(Ct);

        foreach (var seed in CategorySeed.All)
        {
            var category = Assert.Single(all, c => c.Key == seed.Key);
            Assert.Equal((seed.Id, seed.Name, seed.Kind, seed.ParentKey is null ? null : (Guid?)Seed(seed.ParentKey)), (category.Id, category.Name, category.Kind, category.Parent));
        }
        // Top-level ones first.
        Assert.All(all.TakeWhile(c => c.Parent is null), c => Assert.Null(c.Parent));
        Assert.All(all.SkipWhile(c => c.Parent is null), c => Assert.NotNull(c.Parent));
    }

    [Fact]
    public async Task Create_refuses_a_duplicate_sibling_name_case_insensitively()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<ICategoryStore>();

        var duplicate = await store.CreateAsync(Seed("food"), "  courses ", CategoryKind.Expense, Ct);
        Assert.Equal(BankingCodes.CategoryName, Code(duplicate));
        Assert.Equal("name", Assert.Single(FieldErrors.Of(duplicate.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException())!).Path);
        Assert.Equal(BankingCodes.CategoryName, Code(await store.CreateAsync(null, " ", CategoryKind.Expense, Ct)));
        Assert.Equal(BankingCodes.CategoryName, Code(await store.CreateAsync(null, new string('a', 61), CategoryKind.Expense, Ct)));
        // The same name under another parent is fine.
        Assert.Equal("Courses", Value(await store.CreateAsync(Seed("home"), "Courses", CategoryKind.Expense, Ct)).Name);
    }

    [Fact]
    public async Task A_merged_category_frees_its_name_among_its_siblings()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<ICategoryStore>();

        Value(await store.MergeAsync(Seed("food.delivery"), Seed("food.restaurants"), Ct));

        Assert.Equal("Livraison", Value(await store.CreateAsync(Seed("food"), "Livraison", CategoryKind.Expense, Ct)).Name);
    }

    [Fact]
    public async Task Create_under_a_child_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<ICategoryStore>();

        Assert.Equal(BankingCodes.CategoryDepth, Code(await store.CreateAsync(Seed("food.groceries"), "Bio", CategoryKind.Expense, Ct)));
    }

    [Fact]
    public async Task A_child_takes_its_parents_kind()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<ICategoryStore>();

        var created = Value(await store.CreateAsync(Seed("income"), "Primes", CategoryKind.Expense, Ct));

        Assert.Equal((CategoryKind.Income, (Guid?)Seed("income"), (string?)null), (created.Kind, created.Parent, created.Key));
        Assert.Equal(created, await store.FindAsync(created.Id, Ct));
    }

    [Fact]
    public async Task Archiving_a_parent_archives_its_children()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<ICategoryStore>();

        Value(await store.UpdateAsync(Seed("health"), null, null, false, true, Ct));
        Assert.Equal(BankingCodes.CategoryArchived, Code(await store.CreateAsync(Seed("health"), "Dentiste", CategoryKind.Expense, Ct)));

        var all = await store.AllAsync(Ct);
        Assert.All(all.Where(c => c.Key is "health" or "health.care" or "health.insurance"), c => Assert.NotNull(c.ArchivedAt));
        Assert.Null(all.Single(c => c.Key == "food").ArchivedAt);

        // Unarchiving a child brings its parent back, not its sibling.
        Value(await store.UpdateAsync(Seed("health.care"), null, null, false, false, Ct));
        all = await store.AllAsync(Ct);
        Assert.Null(all.Single(c => c.Key == "health").ArchivedAt);
        Assert.NotNull(all.Single(c => c.Key == "health.insurance").ArchivedAt);
    }

    [Fact]
    public async Task Update_renames_and_moves_within_the_rules()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<ICategoryStore>();

        Assert.Equal("Supermarché", Value(await store.UpdateAsync(Seed("food.groceries"), " Supermarché ", null, false, null, Ct)).Name);
        Assert.Equal(BankingCodes.CategoryName, Code(await store.UpdateAsync(Seed("food.delivery"), "supermarché", null, false, null, Ct)));
        Assert.Equal(BankingCodes.CategoryKind, Code(await store.UpdateAsync(Seed("food.delivery"), null, Seed("income"), true, null, Ct)));
        Assert.Equal(BankingCodes.CategoryDepth, Code(await store.UpdateAsync(Seed("food"), null, Seed("home"), true, null, Ct)));
        Assert.Equal(BankingCodes.CategoryDepth, Code(await store.UpdateAsync(Seed("misc"), null, Seed("food.groceries"), true, null, Ct)));

        Assert.Equal(Seed("home"), Value(await store.UpdateAsync(Seed("food.delivery"), null, Seed("home"), true, null, Ct)).Parent);
        Assert.Null(Value(await store.UpdateAsync(Seed("food.delivery"), null, null, true, null, Ct)).Parent);
    }

    [Fact]
    public async Task Merge_moves_lines_merges_same_name_children_and_resolves()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<ICategoryStore>();
        var transactions = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        var target = Value(await store.CreateAsync(null, "Maison et table", CategoryKind.Expense, Ct));
        var courses = Value(await store.CreateAsync(target.Id, "courses", CategoryKind.Expense, Ct));
        var lines = await LinesAsync(scope, app, "a", "b");
        await store.SetForLinesAsync([lines[0]], Seed("food.groceries"), CategorySource.Hand, "person:x", At, true, Ct);
        await store.SetForLinesAsync([lines[1]], courses.Id, CategorySource.Link, "job", At, false, Ct);

        var merged = Value(await store.MergeAsync(Seed("food"), target.Id, Ct));

        Assert.Equal(target.Id, merged.MergedInto);
        Assert.NotNull(merged.ArchivedAt);
        var first = (await transactions.FindAsync(lines[0], Ct))!;
        var second = (await transactions.FindAsync(lines[1], Ct))!;
        Assert.Equal((courses.Id, CategorySource.Hand), (first.Category!.Value, first.CategorySource!.Value));
        Assert.Equal((courses.Id, CategorySource.Link), (second.Category!.Value, second.CategorySource!.Value));
        Assert.Equal(courses.Id, await store.ResolveKeyAsync("food.groceries", Ct));
        Assert.Equal(target.Id, await store.ResolveKeyAsync("food", Ct));
        // The other children moved under the target.
        Assert.Equal(target.Id, (await store.FindAsync(Seed("food.restaurants"), Ct))!.Parent);
        Assert.Null(await store.ResolveKeyAsync("nothing.here", Ct));
    }

    [Fact]
    public async Task Merge_skips_merged_children_on_both_sides()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<ICategoryStore>();
        var target = Value(await store.CreateAsync(null, "Maison et table", CategoryKind.Expense, Ct));
        var oldCourses = Value(await store.CreateAsync(target.Id, "Courses", CategoryKind.Expense, Ct));
        var grocery = Value(await store.CreateAsync(target.Id, "Épicerie", CategoryKind.Expense, Ct));
        var delivery = Value(await store.CreateAsync(target.Id, "livraison", CategoryKind.Expense, Ct));
        Value(await store.MergeAsync(oldCourses.Id, grocery.Id, Ct));
        Value(await store.MergeAsync(Seed("food.delivery"), Seed("food.restaurants"), Ct));

        Value(await store.MergeAsync(Seed("food"), target.Id, Ct));

        // The target's merged « Courses » is no match: the source's moves under the target, still itself.
        var groceries = (await store.FindAsync(Seed("food.groceries"), Ct))!;
        Assert.Equal((target.Id, (Guid?)null), (groceries.Parent!.Value, groceries.MergedInto));
        Assert.Equal(Seed("food.groceries"), await store.ResolveKeyAsync("food.groceries", Ct));
        // The source's merged « Livraison » keeps its target, never merged again into the target's « livraison ».
        var merged = (await store.FindAsync(Seed("food.delivery"), Ct))!;
        Assert.Equal((target.Id, Seed("food.restaurants")), (merged.Parent!.Value, merged.MergedInto!.Value));
        Assert.Null((await store.FindAsync(delivery.Id, Ct))!.ArchivedAt);
        Assert.Equal(Seed("food.restaurants"), await store.ResolveKeyAsync("food.delivery", Ct));
    }

    [Fact]
    public async Task A_merged_category_stays_merged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<ICategoryStore>();
        Value(await store.MergeAsync(Seed("donations"), Seed("misc"), Ct));

        Assert.Equal(BankingCodes.CategoryArchived, Code(await store.MergeAsync(Seed("donations"), Seed("taxes"), Ct)));
        Assert.Equal(BankingCodes.CategoryArchived, Code(await store.MergeAsync(Seed("taxes"), Seed("donations"), Ct)));
        Assert.Equal(BankingCodes.CategoryArchived, Code(await store.UpdateAsync(Seed("donations"), null, null, false, false, Ct)));
        Assert.Equal(BankingCodes.CategoryArchived, Code(await store.UpdateAsync(Seed("donations"), null, Seed("leisure"), true, null, Ct)));
        Assert.Equal(Seed("misc"), await store.ResolveKeyAsync("donations", Ct));
    }

    [Fact]
    public async Task Merge_across_kinds_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<ICategoryStore>();

        Assert.Equal(BankingCodes.CategoryKind, Code(await store.MergeAsync(Seed("misc"), Seed("income.other"), Ct)));
    }

    [Fact]
    public async Task Merge_a_parent_into_a_child_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<ICategoryStore>();

        Assert.Equal(BankingCodes.CategoryDepth, Code(await store.MergeAsync(Seed("food"), Seed("home.upkeep"), Ct)));
        Assert.Equal(BankingCodes.CategoryDepth, Code(await store.MergeAsync(Seed("food"), Seed("food"), Ct)));
    }

    [Fact]
    public async Task SetForLines_respects_precedence()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<ICategoryStore>();
        var transactions = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        var lines = await LinesAsync(scope, app, "a", "b");

        Assert.Equal(1, await store.SetForLinesAsync([lines[0]], Seed("misc"), CategorySource.Hand, "person:x", At, true, Ct));
        Assert.Equal(1, await store.SetForLinesAsync([lines[1]], Seed("misc"), CategorySource.Rule, "rule:x", At, false, Ct));

        // A link replaces a rule's category, never a hand one.
        Assert.Equal(1, await store.SetForLinesAsync(lines, Seed("leisure.subscriptions"), CategorySource.Link, "job", At, false, Ct));
        Assert.Equal((Seed("misc"), CategorySource.Hand), ((await transactions.FindAsync(lines[0], Ct))!.Category!.Value, (await transactions.FindAsync(lines[0], Ct))!.CategorySource!.Value));
        Assert.Equal(Seed("leisure.subscriptions"), (await transactions.FindAsync(lines[1], Ct))!.Category);
        // A rule does not replace a link's.
        Assert.Equal(0, await store.SetForLinesAsync([lines[1]], Seed("misc"), CategorySource.Rule, "rule:x", At, false, Ct));

        // Clearing the link's leaves the hand one.
        Assert.Equal(1, await store.ClearSourceAsync(lines, [CategorySource.Link], Ct));
        Assert.Null((await transactions.FindAsync(lines[1], Ct))!.CategorySource);

        // A person replaces a hand category; a link never replaces an assistant one.
        Assert.Equal(1, await store.SetForLinesAsync([lines[0]], Seed("bank"), CategorySource.Hand, "person:y", At, true, Ct));
        Assert.Equal(Seed("bank"), (await transactions.FindAsync(lines[0], Ct))!.Category);
        Assert.Equal(1, await store.SetForLinesAsync([lines[1]], Seed("bank"), CategorySource.Assistant, "person:y", At, true, Ct));
        Assert.Equal(0, await store.SetForLinesAsync([lines[1]], Seed("misc"), CategorySource.Link, "job", At, false, Ct));
        Assert.Equal(Seed("bank"), (await transactions.FindAsync(lines[1], Ct))!.Category);

        // A clear by no person leaves a person's choice; « Laisser Nid décider » by a person clears it.
        Assert.Equal(0, await store.SetForLinesAsync(lines, null, null, "job", At, false, Ct));
        Assert.Equal(1, await store.SetForLinesAsync([lines[0]], null, null, "person:x", At, true, Ct));
        var cleared = (await transactions.FindAsync(lines[0], Ct))!;
        Assert.Equal((null, null), (cleared.Category, cleared.CategorySource));
    }
}
