using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Ai.Contracts;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Banking.Infrastructure.Persistence.Migrations;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Banking.IntegrationTests;

/// <summary>#449: categories suggested for uncategorised lines from what a person chose for lines of the same pattern.</summary>
public class CategorySuggestionTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Suggestions = $"{BankingApi.Root}/category-suggestions";
    private const string Dismissals = $"{Suggestions}/dismissals";

    private static Guid Seed(string key) => CategorySeed.Find(key)!.Id;

    private static readonly Guid Groceries = Seed("food.groceries");

    private sealed record Setup(NidApp App, Guid Shared, Guid OwnA, Guid OwnB) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>
    /// A's « Courant A », on an envelope of A's that B reads but does not write; A's « Perso A », which B cannot read; B's « Courant B »,
    /// which A cannot read.
    /// </summary>
    private static async Task<Setup> StartAsync(PostgresFixture fixture, Labels? labels = null)
    {
        var app = await BankingApi.StartAsync(fixture,
            overrides: labels is null ? null : services => services.Replace(ServiceDescriptor.Scoped<IEmbeddings>(_ => labels)));
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Courant A"), BankingApi.Account("uid-2", "hash-2", null, "Perso A"));
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-3", "hash-3", null, "Courant B"));
        var accounts = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray()
            .Concat((await BankingApi.AccountsAsync(app, app.AdultB)).EnumerateArray())
            .ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("id").GetGuid());
        var envelope = await app.NewEnvelopeAsync("Courant", app.AdultA, Visibility.Shared);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{accounts["Courant A"]}",
            new { envelopeId = envelope.Value })).StatusCode);
        return new Setup(app, accounts["Courant A"], accounts["Perso A"], accounts["Courant B"]);
    }

    private static int _reference;

    private static async Task<Guid> AddAsync(NidApp app, Guid account, string label, decimal amount = -12.5m)
    {
        var reference = $"ref-{Interlocked.Increment(ref _reference)}";
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        await store.AddAsync(account, [new NewBankTransaction(reference, new LocalDate(2026, 10, 5), null, amount, Currency.Eur, label, null, null, "api")], default);
        return (await store.ListAsync([account], null, null, 0, 1000, default)).Single(t => t.ProviderRef == reference).Id;
    }

    private static async Task HandAsync(NidApp app, PersonId person, Guid line, Guid category)
        => Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(person, HttpMethod.Put, $"{BankingApi.Root}/transactions/{line}/category", new { categoryId = category })).StatusCode);

    private static async Task SourceAsync(NidApp app, Guid line, Guid category, CategorySource source)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<ICategoryStore>()
            .SetForLinesAsync([line], category, source, "job", SystemClock.Instance.GetCurrentInstant(), false, default);
    }

    private static async Task<JsonElement> PageAsync(NidApp app, PersonId person)
        => JsonDocument.Parse(await app.GetRawAsync(person, Suggestions)).RootElement;

    private static JsonElement[] Groups(JsonElement page) => [.. page.GetProperty("items").EnumerateArray()];

    private static JsonElement? Group(JsonElement page, string pattern, string direction = "debit")
        => Groups(page).Cast<JsonElement?>().SingleOrDefault(g => g!.Value.GetProperty("pattern").GetString() == pattern && g.Value.GetProperty("direction").GetString() == direction);

    [Fact]
    public async Task Hand_choices_of_a_pattern_suggest_their_category_for_its_uncategorised_lines()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        foreach (var label in new[] { "CB SHOP ONE 12/10", "CB SHOP ONE 13/10" })
            await HandAsync(app, app.AdultA, await AddAsync(app, s.Shared, label), Groceries);
        var open = await AddAsync(app, s.Shared, "CB SHOP ONE 20/10");
        // A credit of the same pattern is backed by no credit.
        await AddAsync(app, s.Shared, "SHOP ONE REMBOURSEMENT", 5m);
        // Only a person's choice counts: the assistant's does, a rule's or a hint's does not.
        foreach (var label in new[] { "THIRD SHOP 1", "THIRD SHOP 2" })
            await SourceAsync(app, await AddAsync(app, s.Shared, label), Groceries, CategorySource.Assistant);
        await AddAsync(app, s.Shared, "THIRD SHOP 3");
        await SourceAsync(app, await AddAsync(app, s.Shared, "RULED SHOP 1"), Groceries, CategorySource.Rule);
        await SourceAsync(app, await AddAsync(app, s.Shared, "RULED SHOP 2"), Groceries, CategorySource.Hint);
        await AddAsync(app, s.Shared, "RULED SHOP 3");
        // 3 of 4 agree: under 80 %.
        foreach (var category in new[] { Groceries, Groceries, Groceries, Seed("food.restaurants") })
            await HandAsync(app, app.AdultA, await AddAsync(app, s.Shared, "MIXED PLACE"), category);
        await AddAsync(app, s.Shared, "MIXED PLACE");
        // A single hand line is not enough.
        await HandAsync(app, app.AdultA, await AddAsync(app, s.Shared, "LONE SHOP"), Groceries);
        await AddAsync(app, s.Shared, "LONE SHOP");

        var page = await PageAsync(app, app.AdultA);
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        var shop = Group(page, "SHOP ONE")!.Value;
        Assert.Equal((Groceries, 1, 2, 100), (shop.GetProperty("categoryId").GetGuid(), shop.GetProperty("lineCount").GetInt32(),
            shop.GetProperty("evidenceCount").GetInt32(), shop.GetProperty("agreement").GetInt32()));
        Assert.Equal(open, Assert.Single(shop.GetProperty("lines").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.NotNull(Group(page, "THIRD SHOP"));
        Assert.Null(Group(page, "SHOP ONE", "credit"));

        // The group covering the most lines comes first.
        await AddAsync(app, s.Shared, "THIRD SHOP 4");
        Assert.Equal(["THIRD SHOP", "SHOP ONE"], Groups(await PageAsync(app, app.AdultA)).Select(g => g.GetProperty("pattern").GetString()));

        // « Catégoriser ces opérations »: by hand, so the group is gone.
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/transactions/category",
            new { transactionIds = new[] { open }, categoryId = Groceries })).StatusCode);
        Assert.Null(Group(await PageAsync(app, app.AdultA), "SHOP ONE"));

        // A rule that matches the lines takes them at the next pass: they leave the group.
        Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/category-rules",
            new { pattern = "THIRD", categoryId = Groceries })).StatusCode);
        Assert.Equal(0, (await PageAsync(app, app.AdultA)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_person_sees_only_groups_of_accounts_they_read_and_lines_they_write()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        // Evidence on the shared account, which B reads; lines on it, which B does not write.
        foreach (var label in new[] { "SHOP ONE 1", "SHOP ONE 2" })
            await HandAsync(app, app.AdultA, await AddAsync(app, s.Shared, label), Groceries);
        await AddAsync(app, s.Shared, "SHOP ONE 3");
        await AddAsync(app, s.OwnB, "SHOP ONE 4");
        // Evidence only on A's own account, which B cannot read.
        foreach (var label in new[] { "SECRET SHOP 1", "SECRET SHOP 2" })
            await HandAsync(app, app.AdultA, await AddAsync(app, s.OwnA, label), Groceries);
        await AddAsync(app, s.OwnB, "SECRET SHOP 3");

        var b = Assert.Single(Groups(await PageAsync(app, app.AdultB)));
        Assert.Equal(("SHOP ONE", 1, 2), (b.GetProperty("pattern").GetString(), b.GetProperty("lineCount").GetInt32(), b.GetProperty("evidenceCount").GetInt32()));
        Assert.Equal("Courant B", Assert.Single(b.GetProperty("lines").EnumerateArray()).GetProperty("accountName").GetString());
        // A writes neither B's account nor reads it: only the shared line.
        var a = Assert.Single(Groups(await PageAsync(app, app.AdultA)));
        Assert.Equal(("SHOP ONE", 1), (a.GetProperty("pattern").GetString(), a.GetProperty("lineCount").GetInt32()));
        Assert.Equal("Courant A", Assert.Single(a.GetProperty("lines").EnumerateArray()).GetProperty("accountName").GetString());

        // A dismissal tells of a pattern: B sees one whose pattern is on a line he reads, not one only A's own account has.
        await AddAsync(app, s.OwnA, "PRIVATE PLACE");
        var hidden = await DismissAsync(app, app.AdultA, "PRIVATE PLACE", Groceries);
        var shown = await DismissAsync(app, app.AdultA, "SHOP ONE", Groceries);
        Assert.Equal([shown], (await BankingApi.JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Get, Dismissals))).EnumerateArray().Select(d => d.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Dismissals}/{hidden}")).StatusCode);
    }

    private static async Task<Guid> DismissAsync(NidApp app, PersonId person, string pattern, Guid category, string direction = "debit")
    {
        var response = await app.RawAsync(person, HttpMethod.Post, Dismissals, new { pattern, direction, categoryId = category });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await BankingApi.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Ignore_hides_a_group_for_the_household_until_withdrawn_and_follows_a_merge()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        foreach (var label in new[] { "SHOP ONE 1", "SHOP ONE 2" })
            await HandAsync(app, app.AdultA, await AddAsync(app, s.Shared, label), Groceries);
        await AddAsync(app, s.Shared, "SHOP ONE 3");
        await AddAsync(app, s.OwnB, "SHOP ONE 4");

        var dismissal = await DismissAsync(app, app.AdultA, "shop one", Groceries);
        Assert.Equal(dismissal, await DismissAsync(app, app.AdultA, "SHOP ONE", Groceries));
        Assert.Equal(0, (await PageAsync(app, app.AdultA)).GetProperty("total").GetInt32());
        Assert.Equal(0, (await PageAsync(app, app.AdultB)).GetProperty("total").GetInt32());
        // New evidence does not bring it back.
        await HandAsync(app, app.AdultA, await AddAsync(app, s.Shared, "SHOP ONE 5"), Groceries);
        Assert.Equal(0, (await PageAsync(app, app.AdultA)).GetProperty("total").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Dismissals}/{dismissal}")).StatusCode);
        Assert.Equal(1, (await PageAsync(app, app.AdultA)).GetProperty("total").GetInt32());

        // A merge carries the dismissals of the merged category to its target; one the target has already stays single.
        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/categories", new { name = "Épicerie", kind = "expense" });
        var merged = (await BankingApi.JsonAsync(created)).GetProperty("id").GetGuid();
        await DismissAsync(app, app.AdultA, "SHOP ONE", merged);
        await DismissAsync(app, app.AdultA, "OTHER SHOP", merged);
        await DismissAsync(app, app.AdultA, "OTHER SHOP", Groceries);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/categories/{merged}/merge", new { into = Groceries })).StatusCode);
        var listed = (await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Dismissals))).EnumerateArray()
            .Select(d => (d.GetProperty("pattern").GetString(), d.GetProperty("categoryId").GetGuid())).ToList();
        Assert.Equal([("OTHER SHOP", Groceries), ("SHOP ONE", Groceries)], listed);
        Assert.Equal(0, (await PageAsync(app, app.AdultA)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_dismissal_names_a_valid_pattern_and_a_category_in_use()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        Task<HttpResponseMessage> Post(object body) => app.RawAsync(app.AdultA, HttpMethod.Post, Dismissals, body);
        await BankingApi.AssertRefusedAsync(await Post(new { pattern = "x", direction = "debit", categoryId = Groceries }), HttpStatusCode.UnprocessableEntity, BankingCodes.RulePattern);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(new { pattern = "SHOP", direction = "both", categoryId = Groceries })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(new { pattern = "SHOP", direction = "debit", categoryId = Guid.NewGuid() })).StatusCode);
        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/categories", new { name = "Ancienne", kind = "expense" });
        var archived = (await BankingApi.JsonAsync(created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/categories/{archived}", new { archived = true })).StatusCode);
        await BankingApi.AssertRefusedAsync(await Post(new { pattern = "SHOP", direction = "debit", categoryId = archived }), HttpStatusCode.UnprocessableEntity, BankingCodes.CategoryArchived);
    }

    /// <summary>The latest backfill (#457) writes the pattern <see cref="CategoryRules.Suggest"/> gives at import, and clears a stale one.</summary>
    [Fact]
    public async Task The_backfill_writes_the_pattern_import_writes()
    {
        await using var s = await StartAsync(fixture);
        string[] labels =
        [
            "CB CARREFOUR CITY 12/10", "CARTE X1234 11/10 AMAZON EU SARL", "PRLV SEPA EDF CLIENTS PARTICULIE", "VIR SEPA SOCIETE EXEMPLE SAS",
            "CARTE 12/03/26 BOULANGERIE DU COIN", "CARTE 12/03/2026 CB VIR INSTANT", "NETFLIX 123 PARIS", "12/10 X", "X 1", "AB", "VIR SEPA",
            "Prélèvement Free Mobile", "PAIEMENT PAR CARTE MONOP", "CHÈQUE 1234567", "E2E SHOP", "ÉCOLE MATERNELLE ÉTÉ", "SHOP  ONE",
            "VIR DE BOB DEV", "PRLV SPOTIFY P2A4F81C", "VIR VERS COMPTE JOINT", "VIREMENT SEPA RECU ENTREPRISE EXEMPLE", "VIR INST EMIS VERS BOB",
            "PRLV ECHEANCE PRET IMMO", "BOULANGERIE DE LA GARE", "CARTE 12/03/26 VIR DE SHOP", "DE DU VERS", "VIR DE 1234 BOB", "VIREMENTS RECUS",
        ];
        foreach (var label in labels)
            await AddAsync(s.App, s.Shared, label);
        await using var db = new BankingDbContext(new DbContextOptionsBuilder<BankingDbContext>().Configure(s.App.ConnectionString).Options);
        var imported = await db.Transactions.AsNoTracking().ToDictionaryAsync(t => t.Id, t => (t.NormalizedLabel, t.SuggestedPattern), TestContext.Current.CancellationToken);
        Assert.All(imported.Values, t => Assert.Equal(CategoryRules.Suggest(t.NormalizedLabel), t.SuggestedPattern));

        await db.Transactions.ExecuteUpdateAsync(u => u.SetProperty(t => t.SuggestedPattern, "STALE"), TestContext.Current.CancellationToken);
        // As the migration runs it: braces are no format placeholders there.
        await db.Database.ExecuteSqlRawAsync(GenericBankPrefixes457.Backfill.Replace("{", "{{").Replace("}", "}}"), TestContext.Current.CancellationToken);
        Assert.Equal(imported, await db.Transactions.AsNoTracking().ToDictionaryAsync(t => t.Id, t => (t.NormalizedLabel, t.SuggestedPattern), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_pattern_with_no_history_takes_the_category_of_patterns_near_it_by_meaning()
    {
        var labels = new Labels();
        await using var s = await StartAsync(fixture, labels);
        var app = s.App;
        foreach (var label in new[] { "CB SUPER MARKET 1", "CB SUPER MARKET 2" })
            await HandAsync(app, app.AdultA, await AddAsync(app, s.Shared, label), Groceries);
        var open = await AddAsync(app, s.Shared, "CB CITY MARKET 3");
        await AddAsync(app, s.Shared, "CB GARAGE AUTO 4");
        var name = await NameAsync(app, app.AdultA);
        await AddAsync(app, s.Shared, $"VIR {name}");

        await IndexAsync(app);

        // A household member's name never leaves; the patterns go as data of the class bank_labels, which a cloud provider takes only when allowed.
        var sent = labels.Requests.SelectMany(r => r.Texts).ToList();
        Assert.Equal(["CITY MARKET", "GARAGE AUTO", "SUPER MARKET"], sent.Order());
        Assert.All(labels.Requests, r =>
        {
            Assert.Equal(DataClasses.BankLabels, r.DataClass);
            Assert.Equal([DataClasses.BankLabels], r.Kinds!);
        });
        var page = await PageAsync(app, app.AdultA);
        var city = Group(page, "CITY MARKET")!.Value;
        Assert.Equal((Groceries, 2, true), (city.GetProperty("categoryId").GetGuid(), city.GetProperty("evidenceCount").GetInt32(), city.GetProperty("byMeaning").GetBoolean()));
        Assert.Equal(open, Assert.Single(city.GetProperty("lines").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Null(Group(page, "GARAGE AUTO"));
        // A pass with nothing new sends nothing.
        labels.Requests.Clear();
        await IndexAsync(app);
        Assert.Empty(labels.Requests);

        // Ignored, it does not come back.
        await DismissAsync(app, app.AdultA, "CITY MARKET", Groceries);
        Assert.Null(Group(await PageAsync(app, app.AdultA), "CITY MARKET"));
    }

    [Fact]
    public async Task Meaning_counts_only_the_reader_lines_and_nothing_runs_while_the_use_is_off()
    {
        var labels = new Labels { On = false };
        await using var s = await StartAsync(fixture, labels);
        var app = s.App;
        // Evidence on A's own account, which B cannot read.
        foreach (var label in new[] { "CB SUPER MARKET 1", "CB SUPER MARKET 2" })
            await HandAsync(app, app.AdultA, await AddAsync(app, s.OwnA, label), Groceries);
        await AddAsync(app, s.OwnB, "CB CITY MARKET 3");

        await IndexAsync(app);
        Assert.Empty(labels.Requests);

        labels.On = true;
        await IndexAsync(app);
        Assert.NotEmpty(labels.Requests);
        Assert.Equal(0, (await PageAsync(app, app.AdultB)).GetProperty("total").GetInt32());
    }

    private static async Task IndexAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<PatternIndexer>().RunOnceAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> NameAsync(NidApp app, PersonId person)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var info = (await scope.ServiceProvider.GetRequiredService<IPersonRegistry>().ListAsync(TestContext.Current.CancellationToken)).Single(p => p.Id == person);
        return info.DisplayName.Split(' ')[0];
    }

    /// <summary>The <c>embed</c> use with one topic, shops of food (« MARKET »), and everything else; <see cref="On"/> is the <c>decide</c> use.</summary>
    private sealed class Labels : IEmbeddings
    {
        public bool On { get; set; } = true;

        public List<LlmEmbedRequest> Requests { get; } = [];

        public Task<string?> ModelAsync(CancellationToken ct) => Task.FromResult<string?>("fake-embed");

        public Task<bool> UseOnAsync(string use, CancellationToken ct) => Task.FromResult(use == ModelUses.Decide && On);

        public Task<Result<LlmEmbeddings>> EmbedAsync(LlmEmbedRequest request, CancellationToken ct)
        {
            lock (Requests)
                Requests.Add(request);
            return Task.FromResult(Result.Success(new LlmEmbeddings("fake-embed", 3,
                [.. request.Texts.Select(t => (IReadOnlyList<float>)(t.Contains("MARKET", StringComparison.Ordinal) ? [1f, 0f, 0.1f] : [0f, 1f, 0.1f]))])));
        }
    }
}
