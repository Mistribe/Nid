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
            overrides: labels is null ? null : services =>
            {
                services.Replace(ServiceDescriptor.Scoped<IEmbeddings>(_ => labels));
                services.Replace(ServiceDescriptor.Scoped<IModelDigests>(_ => labels));
                services.Replace(ServiceDescriptor.Scoped<IDecisions>(_ => labels));
            });
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

    private static async Task<Guid> AddAsync(NidApp app, Guid account, string label, decimal amount = -12.5m, string? counterparty = null)
    {
        var reference = $"ref-{Interlocked.Increment(ref _reference)}";
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        await store.AddAsync(account, [new NewBankTransaction(reference, new LocalDate(2026, 10, 5), null, amount, Currency.Eur, label, counterparty, null, "api")], default);
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

        // A household member's name never leaves; the patterns, and the categories' names (transfers never are options), go as data of the
        // class bank_labels, which a cloud provider takes only when allowed.
        var sent = labels.Requests.SelectMany(r => r.Texts).ToList();
        Assert.Equal(["CITY MARKET", "GARAGE AUTO", "SUPER MARKET"], sent.Where(t => t == t.ToUpperInvariant()).Order());
        Assert.Contains("Alimentation › Courses", sent);
        Assert.DoesNotContain(sent, t => t.StartsWith("Mouvements internes", StringComparison.Ordinal));
        Assert.All(labels.Requests, r =>
        {
            Assert.Equal(DataClasses.BankLabels, r.DataClass);
            Assert.Equal([DataClasses.BankLabels], r.Kinds!);
        });
        // The model is not calibrated: nothing by meaning.
        Assert.Null(Group(await PageAsync(app, app.AdultA), "CITY MARKET"));

        await ProfileAsync(app, threshold: 0.8, cold: null);
        var page = await PageAsync(app, app.AdultA);
        var city = Group(page, "CITY MARKET")!.Value;
        Assert.Equal((Groceries, 2, "meaning"), (city.GetProperty("categoryId").GetGuid(), city.GetProperty("evidenceCount").GetInt32(), city.GetProperty("basis").GetString()));
        Assert.Equal(open, Assert.Single(city.GetProperty("lines").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Null(Group(page, "GARAGE AUTO"));

        // Calibrated for the cold start too: a pattern no one's lines decide takes the category whose name it is near.
        await ProfileAsync(app, threshold: 0.8, cold: 0.9);
        var garage = Group(await PageAsync(app, app.AdultA), "GARAGE AUTO")!.Value;
        Assert.Equal((Seed("transport.vehicle"), 0, 100, "category_name"), (garage.GetProperty("categoryId").GetGuid(), garage.GetProperty("evidenceCount").GetInt32(),
            garage.GetProperty("agreement").GetInt32(), garage.GetProperty("basis").GetString()));
        // A re-pulled model is another build: its profile is not this one.
        labels.Digest = "sha256:other";
        Assert.Null(Group(await PageAsync(app, app.AdultA), "CITY MARKET"));
        labels.Digest = "sha256:fake";
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
        await ProfileAsync(app, threshold: 0.8, cold: null);

        await IndexAsync(app);
        Assert.Empty(labels.Requests);

        labels.On = true;
        await IndexAsync(app);
        Assert.NotEmpty(labels.Requests);
        Assert.Equal(0, (await PageAsync(app, app.AdultB)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Lines_to_classify_list_the_writable_uncategorised_lines_each_with_its_suggestion()
    {
        var labels = new Labels();
        await using var s = await StartAsync(fixture, labels);
        var app = s.App;
        foreach (var label in new[] { "CB SHOP ONE 1", "CB SHOP ONE 2", "CB SUPER MARKET 1", "CB SUPER MARKET 2" })
            await HandAsync(app, app.AdultA, await AddAsync(app, s.Shared, label), Groceries);
        var byHistory = await AddAsync(app, s.Shared, "CB SHOP ONE 3");
        var byMeaning = await AddAsync(app, s.OwnA, "CB CITY MARKET 3");
        var none = await AddAsync(app, s.OwnA, "CB GARAGE AUTO 4");
        await AddAsync(app, s.OwnB, "CB SHOP ONE 5");
        await IndexAsync(app);
        await ProfileAsync(app, threshold: 0.8, cold: null);

        var page = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{BankingApi.Root}/lines-to-classify")).RootElement;
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        var items = page.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("line").GetProperty("id").GetGuid(), i => i.GetProperty("suggestion"));
        Assert.Equal([byHistory, byMeaning, none], items.Keys.Order());
        Assert.Equal((Groceries, "history", "SHOP ONE"), (items[byHistory].GetProperty("categoryId").GetGuid(), items[byHistory].GetProperty("basis").GetString(),
            items[byHistory].GetProperty("pattern").GetString()));
        Assert.Equal("meaning", items[byMeaning].GetProperty("basis").GetString());
        Assert.Equal(JsonValueKind.Null, items[none].ValueKind);
        // A's own accounts are the filter's options, not B's.
        Assert.Equal(["Courant A", "Perso A"], page.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("name").GetString()).Order());

        var own = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{BankingApi.Root}/lines-to-classify?account={s.OwnA}")).RootElement;
        Assert.Equal(2, own.GetProperty("total").GetInt32());
        // An account A does not write lists nothing; B's lines are B's to classify.
        Assert.Equal(0, JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{BankingApi.Root}/lines-to-classify?account={s.OwnB}")).RootElement.GetProperty("total").GetInt32());
        var b = JsonDocument.Parse(await app.GetRawAsync(app.AdultB, $"{BankingApi.Root}/lines-to-classify")).RootElement;
        Assert.Equal(1, b.GetProperty("total").GetInt32());
    }

    private const string Calibrations = $"{Suggestions}/calibrations";

    [Fact]
    public async Task An_admin_calibrates_the_model_on_the_evaluation_set_and_any_adult_reads_its_profile()
    {
        var labels = new Labels();
        await using var s = await StartAsync(fixture, labels);
        var app = s.App;

        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Post, Calibrations)).StatusCode);
        var queued = await QueueAsync(app);
        Assert.Equal("queued", queued.GetProperty("status").GetString());
        // Asked again while queued: the same one.
        Assert.Equal(queued.GetProperty("id").GetGuid(), (await QueueAsync(app)).GetProperty("id").GetGuid());

        await CalibrateAsync(app);

        var view = JsonDocument.Parse(await app.GetRawAsync(app.AdultB, Calibrations)).RootElement;
        Assert.Equal(("nearest-neighbours", "fake-embed", "sha256:fake", "done"), (view.GetProperty("implementation").GetString(),
            view.GetProperty("model").GetString(), view.GetProperty("digest").GetString(), view.GetProperty("latest").GetProperty("status").GetString()));
        var profile = view.GetProperty("profile");
        Assert.Equal((CategoryEvalSet.Current.Cases.Count, 5), (profile.GetProperty("cases").GetInt32(), profile.GetProperty("neighbours").GetInt32()));
        // Invented labels: no household data goes, and every text of the set went once.
        Assert.All(labels.Requests, r => Assert.Equal((DataClasses.BankLabels, 0), (r.DataClass, r.Concerned!.Count)));
        Assert.Contains("LIDL", labels.Requests.SelectMany(r => r.Texts));
        // Nothing left queued: a pass sends nothing.
        labels.Requests.Clear();
        await CalibrateAsync(app);
        Assert.Empty(labels.Requests);

        // The model changes while it runs: failed, with the gateway's code, and the profile stays the one measured before.
        await QueueAsync(app);
        labels.Answering = "another-embed";
        await CalibrateAsync(app);
        view = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, Calibrations)).RootElement;
        Assert.Equal(("failed", JobCodes.ModelUnavailable), (view.GetProperty("latest").GetProperty("status").GetString(),
            view.GetProperty("latest").GetProperty("code").GetString()));
        Assert.Equal(JsonValueKind.Object, view.GetProperty("profile").ValueKind);

        // No digest the workers agree on: nothing to calibrate, and nothing in force.
        labels.Digest = null;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.RawAsync(app.AdultA, HttpMethod.Post, Calibrations)).StatusCode);
        view = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, Calibrations)).RootElement;
        Assert.Equal((JobCodes.ModelUnavailable, JsonValueKind.Null), (view.GetProperty("unavailable").GetString(), view.GetProperty("profile").ValueKind));

        // The decide use off: that is the reason given, not a missing model, and nothing is queued.
        labels.On = false;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.RawAsync(app.AdultA, HttpMethod.Post, Calibrations)).StatusCode);
        view = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, Calibrations)).RootElement;
        Assert.Equal(JobCodes.UseOff, view.GetProperty("unavailable").GetString());
    }

    private static async Task<JsonElement> QueueAsync(NidApp app)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Calibrations);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static async Task CalibrateAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<DecideCalibrator>().RunOnceAsync(TestContext.Current.CancellationToken);
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

    /// <summary>#450: the profile a calibration of the fake model would have left, at these thresholds.</summary>
    private static async Task ProfileAsync(NidApp app, double? threshold, double? cold)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<IDecideCalibrationStore>().SaveProfileAsync(new DecideProfile(
            new DecideKey(DecideCalibration.NearestNeighbours, DecideCalibration.NearestNeighboursVersion, "fake-embed", "sha256:fake"), 1, 40, threshold, cold,
            null, false, new DecideCalibration.Results(400, CategorySuggestions.Neighbours, null, null), SystemClock.Instance.GetCurrentInstant()),
            TestContext.Current.CancellationToken);
    }

    private static (Guid Category, string Basis)? Suggested(JsonElement page, string pattern, string direction = "debit")
        => Group(page, pattern, direction) is { } group ? (group.GetProperty("categoryId").GetGuid(), group.GetProperty("basis").GetString()!) : null;

    /// <summary>#450: a dismissed pick lets the next basis try: meaning, then the category's name, then the model's guess.</summary>
    [Fact]
    public async Task A_dismissed_pick_lets_the_next_basis_try()
    {
        var labels = new Labels { DecideModel = "fake-decide" };
        await using var s = await StartAsync(fixture, labels);
        var app = s.App;
        var fuel = Seed("transport.fuel");
        foreach (var label in new[] { "CB GARAGE ONE 1", "CB GARAGE ONE 2" })
            await HandAsync(app, app.AdultA, await AddAsync(app, s.Shared, label), fuel);
        await AddAsync(app, s.Shared, "CB GARAGE SPORT 3");
        await IndexAsync(app);
        await ProfileAsync(app, threshold: 0.8, cold: 0.9);
        await WorkerProfileAsync(app, threshold: 0.5);
        await GuessAsync(app);

        Assert.Equal((fuel, "meaning"), Suggested(await PageAsync(app, app.AdultA), "GARAGE SPORT"));
        await DismissAsync(app, app.AdultA, "GARAGE SPORT", fuel);
        Assert.Equal((Seed("transport.vehicle"), "category_name"), Suggested(await PageAsync(app, app.AdultA), "GARAGE SPORT"));
        await DismissAsync(app, app.AdultA, "GARAGE SPORT", Seed("transport.vehicle"));
        Assert.Equal((Seed("leisure.sport"), "ai"), Suggested(await PageAsync(app, app.AdultA), "GARAGE SPORT"));
        await DismissAsync(app, app.AdultA, "GARAGE SPORT", Seed("leisure.sport"));
        Assert.Null(Suggested(await PageAsync(app, app.AdultA), "GARAGE SPORT"));
    }

    [Fact]
    public async Task The_model_guesses_what_nothing_else_decides_from_masked_text_and_each_reader_sees_it_on_their_own_lines()
    {
        var labels = new Labels { DecideModel = "fake-decide" };
        await using var s = await StartAsync(fixture, labels);
        var app = s.App;
        var name = await NameAsync(app, app.AdultA);
        foreach (var label in new[] { "CB SHOP ONE 1", "CB SHOP ONE 2" })
            await HandAsync(app, app.AdultA, await AddAsync(app, s.Shared, label), Groceries);
        await AddAsync(app, s.Shared, "CB SHOP ONE 3");
        await AddAsync(app, s.Shared, "CB COURSES EXPRESS 0412 FR76 3000 6000 0112 3456 7890 189");
        await AddAsync(app, s.OwnA, "PRLV MUTUELLE SANTE 123456789", counterparty: "MUTUELLE EXEMPLE");
        await AddAsync(app, s.OwnB, "CB COURSES MARCHE 5");
        await AddAsync(app, s.Shared, "SALAIRE EXEMPLE", amount: 1500m);
        await AddAsync(app, s.Shared, $"VIR {name}");
        await AddAsync(app, s.Shared, "LOYER APPARTEMENT", counterparty: name);

        // Not calibrated: nothing is asked.
        await GuessAsync(app);
        Assert.Empty(labels.Decisions);

        await WorkerProfileAsync(app, threshold: 0.5);
        await GuessAsync(app);

        // Step A decides SHOP ONE; a household name, in the pattern or as the counterparty, never goes; numbers and IBANs are masked.
        var texts = labels.Decisions.SelectMany(d => d.Texts).ToList();
        Assert.Equal(4, texts.Count);
        Assert.DoesNotContain(texts, t => t.Contains("SHOP ONE", StringComparison.Ordinal) || t.Contains("LOYER", StringComparison.Ordinal)
            || t.Contains(name, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Sens : débit\nMotif : COURSES EXPRESS\nLibellé : CB COURSES EXPRESS 0412 [IBAN]", texts);
        Assert.Contains("Sens : débit\nMotif : MUTUELLE SANTE\nLibellé : PRLV MUTUELLE SANTE [N]\nContrepartie : MUTUELLE EXEMPLE", texts);
        Assert.All(labels.Decisions, d =>
        {
            Assert.Equal(DataClasses.BankLabels, d.DataClass);
            Assert.Equal("Aucune de ces catégories", d.Options[^1]);
        });
        // Each request concerns the readers of its lines' accounts: the shared one A's and B's, a private one its owner's.
        IReadOnlyList<Guid> Concerned(string pattern) => labels.Decisions.Single(d => d.Texts.Any(t => t.Contains(pattern, StringComparison.Ordinal))).Concerned!;
        Assert.Equal(new[] { app.AdultA.Value, app.AdultB.Value }.Order(), Concerned("COURSES EXPRESS"));
        Assert.Equal([app.AdultA.Value], Concerned("MUTUELLE SANTE"));
        Assert.Equal([app.AdultB.Value], Concerned("COURSES MARCHE"));
        var credit = labels.Decisions.Single(d => d.Texts.Any(t => t.Contains("SALAIRE", StringComparison.Ordinal))).Options;
        Assert.Contains("Revenus › Salaire", credit);
        Assert.DoesNotContain("Alimentation › Courses", credit);

        // Shown to whoever writes the lines, at the model's probability; B reads the shared account but does not write it.
        var a = await PageAsync(app, app.AdultA);
        var express = Group(a, "COURSES EXPRESS")!.Value;
        Assert.Equal((Groceries, "ai", 92, 0, "local"), (express.GetProperty("categoryId").GetGuid(), express.GetProperty("basis").GetString(),
            express.GetProperty("agreement").GetInt32(), express.GetProperty("evidenceCount").GetInt32(), express.GetProperty("placement").GetString()));
        Assert.Equal((Seed("health"), "ai"), Suggested(a, "MUTUELLE SANTE")); // « Santé », the first option sharing a word
        Assert.Equal((Seed("income.salary"), "ai"), Suggested(a, "SALAIRE EXEMPLE", "credit"));
        Assert.Null(Group(a, "COURSES MARCHE"));
        var b = await PageAsync(app, app.AdultB);
        Assert.Equal((Groceries, "ai"), Suggested(b, "COURSES MARCHE"));
        Assert.Null(Group(b, "COURSES EXPRESS"));

        // Kept for the household: a pass with nothing new asks nothing.
        labels.Decisions.Clear();
        await GuessAsync(app);
        Assert.Empty(labels.Decisions);
        // Under the profile's probability, not shown.
        await WorkerProfileAsync(app, threshold: 0.95);
        Assert.Null(Group(await PageAsync(app, app.AdultA), "COURSES EXPRESS"));
        await WorkerProfileAsync(app, threshold: 0.5);

        // A new category changes the options: every guess goes, the next pass asks again.
        await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/categories", new { name = "Épicerie fine", kind = "expense" }));
        Assert.Null(Group(await PageAsync(app, app.AdultA), "COURSES EXPRESS"));
        await GuessAsync(app);
        Assert.Equal(4, labels.Decisions.SelectMany(d => d.Texts).Count());
        Assert.Equal((Groceries, "ai"), Suggested(await PageAsync(app, app.AdultA), "COURSES EXPRESS"));

        // « Suggérer à nouveau », by any adult: forgotten, and asked again at the loop's next tick.
        var signal = app.Services.GetRequiredService<CategoryGuessSignal>();
        signal.TryTake();
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultB, HttpMethod.Post, $"{Suggestions}/refresh")).StatusCode);
        Assert.Null(Group(await PageAsync(app, app.AdultA), "COURSES EXPRESS"));
        Assert.True(signal.TryTake());
    }

    /// <summary>#450: a guess chose among the categories as they were: renaming, archiving, bringing back or merging one forgets them all.</summary>
    [Fact]
    public async Task Renaming_archiving_unarchiving_or_merging_a_category_forgets_every_guess()
    {
        var labels = new Labels { DecideModel = "fake-decide" };
        await using var s = await StartAsync(fixture, labels);
        var app = s.App;
        await AddAsync(app, s.Shared, "CB COURSES EXPRESS 3");
        await WorkerProfileAsync(app, threshold: 0.5);
        var created = (await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/categories",
            new { name = "Épicerie fine", kind = "expense" }))).GetProperty("id").GetGuid();
        Task<HttpResponseMessage> Patch(object body) => app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/categories/{created}", body);

        async Task ForgottenAfterAsync(Func<Task<HttpResponseMessage>> change, bool forgotten = true)
        {
            await GuessAsync(app);
            Assert.NotEmpty(await GuessesAsync(app));
            Assert.Equal(HttpStatusCode.OK, (await change()).StatusCode);
            Assert.Equal(forgotten, (await GuessesAsync(app)).Count == 0);
        }

        await ForgottenAfterAsync(() => Patch(new { name = "Épicerie" }));
        await ForgottenAfterAsync(() => Patch(new { name = "Épicerie" }), forgotten: false); // nothing changed: kept
        await ForgottenAfterAsync(() => Patch(new { archived = true }));
        await ForgottenAfterAsync(() => Patch(new { archived = false }));
        await ForgottenAfterAsync(() => app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/categories/{created}/merge", new { into = Groceries }));
    }

    private static async Task<IReadOnlyList<CategoryGuess>> GuessesAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return await scope.ServiceProvider.GetRequiredService<ICategorySuggestionStore>().GuessesAsync(
            new DecideKey("llm-choice", "1", "fake-decide", Labels.DecideDigest), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_sample_shows_what_would_be_sent_masked_from_the_reader_own_lines()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var name = await NameAsync(app, app.AdultA);
        await AddAsync(app, s.OwnA, "PRLV MUTUELLE SANTE 123456789");
        await AddAsync(app, s.OwnA, $"VIR {name}");
        await AddAsync(app, s.OwnB, "CB COURSES MARCHE 5");

        static async Task<string[]> SampleAsync(NidApp app, PersonId person)
            => [.. JsonDocument.Parse(await app.GetRawAsync(person, $"{Suggestions}/sample")).RootElement.GetProperty("texts").EnumerateArray().Select(t => t.GetString()!)];

        Assert.Equal(["Sens : débit\nMotif : MUTUELLE SANTE\nLibellé : PRLV MUTUELLE SANTE [N]"], await SampleAsync(app, app.AdultA));
        Assert.Equal(["Sens : débit\nMotif : COURSES MARCHE\nLibellé : CB COURSES MARCHE 5"], await SampleAsync(app, app.AdultB));
    }

    [Fact]
    public async Task A_decide_model_is_calibrated_on_the_evaluation_set_as_the_guess_job_asks_it()
    {
        var labels = new Labels { DecideModel = "fake-decide" };
        await using var s = await StartAsync(fixture, labels);
        var app = s.App;

        var signal = app.Services.GetRequiredService<CategoryGuessSignal>();
        signal.TryTake();
        await QueueAsync(app);
        Assert.False(signal.TryTake());
        await CalibrateAsync(app);
        // Done: the guesses are asked at the loop's next tick, not in 5 minutes.
        Assert.True(signal.TryTake());

        var view = JsonDocument.Parse(await app.GetRawAsync(app.AdultB, Calibrations)).RootElement;
        Assert.Equal(("llm-choice", "1", "fake-decide", Labels.DecideDigest, "done"), (view.GetProperty("implementation").GetString(),
            view.GetProperty("version").GetString(), view.GetProperty("model").GetString(), view.GetProperty("digest").GetString(),
            view.GetProperty("latest").GetProperty("status").GetString()));
        var profile = view.GetProperty("profile");
        Assert.Equal((CategoryEvalSet.Current.Cases.Count, false), (profile.GetProperty("cases").GetInt32(), profile.GetProperty("scoreless").GetBoolean()));
        Assert.Equal(JsonValueKind.Number, profile.GetProperty("temperature").ValueKind);
        Assert.NotEmpty(profile.GetProperty("reliability").EnumerateArray());
        // Invented labels: no household data, every case asked once, among the categories of its side and « none of these ».
        Assert.Equal(CategoryEvalSet.Current.Cases.Count, labels.Decisions.Sum(d => d.Texts.Count));
        Assert.All(labels.Decisions, d =>
        {
            Assert.Equal((DataClasses.BankLabels, 0), (d.DataClass, d.Concerned!.Count));
            Assert.Equal("Aucune de ces catégories", d.Options[^1]);
            Assert.True(d.Texts.Count <= CategoryGuesser.Batch);
        });

        // A model with no scores: its answers as they are, no temperature.
        labels.Scoreless = true;
        await QueueAsync(app);
        await CalibrateAsync(app);
        profile = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, Calibrations)).RootElement.GetProperty("profile");
        Assert.Equal((true, JsonValueKind.Null, JsonValueKind.Null), (profile.GetProperty("scoreless").GetBoolean(), profile.GetProperty("temperature").ValueKind,
            profile.GetProperty("threshold").ValueKind));
    }

    private static async Task GuessAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<CategoryGuesser>().RunOnceAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>#450: the profile a calibration of the fake <c>decide</c> model would have left, at this probability, temperature 1.</summary>
    private static async Task WorkerProfileAsync(NidApp app, double threshold)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<IDecideCalibrationStore>().SaveProfileAsync(new DecideProfile(
            new DecideKey("llm-choice", "1", "fake-decide", Labels.DecideDigest), 1, 36, threshold, null, 1.0, false,
            new DecideCalibration.Results(400, null, null, null, Temperature: 1.0), SystemClock.Instance.GetCurrentInstant()),
            TestContext.Current.CancellationToken);
    }


    /// <summary>
    /// The <c>embed</c> use with three topics, markets (« MARKET »), shops (« SHOP ») and cars (« GARAGE », « véhicule »), and everything else;
    /// <see cref="On"/> is the <c>decide</c> use, <see cref="Digest"/> the one the workers declare, <see cref="Answering"/> the model that answers.
    /// #450: <see cref="DecideModel"/> is the <c>decide</c> use's model on a worker (null: none), which puts a pattern in the option sharing a
    /// word with it, else in the last one, « none of these ».
    /// </summary>
    private sealed class Labels : IEmbeddings, IModelDigests, IDecisions
    {
        public const string DecideDigest = "sha256:decide";

        public string? DecideModel { get; set; }

        public bool Scoreless { get; set; }

        public List<LlmDecideRequest> Decisions { get; } = [];

        Task<string?> IDecisions.ModelAsync(CancellationToken ct) => Task.FromResult(DecideModel);

        public Task<LlmDecideImplementation?> ImplementationAsync(string model, CancellationToken ct)
            => Task.FromResult(model == DecideModel ? new LlmDecideImplementation("llm-choice", "1", Scoreless) : null);

        public Task<Result<LlmDecisions>> DecideAsync(LlmDecideRequest request, CancellationToken ct)
        {
            lock (Decisions)
                Decisions.Add(request);
            static HashSet<string> Words(string text)
                => [.. TransactionLabel.Normalize(text).Split([' ', '›'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4)];
            var decisions = request.Texts.Select(text =>
            {
                var pattern = Words(text.Split('\n').Single(l => l.StartsWith("Motif : ", StringComparison.Ordinal))["Motif : ".Length..]);
                var choice = Enumerable.Range(0, request.Options.Count).FirstOrDefault(i => Words(request.Options[i]).Overlaps(pattern), request.Options.Count - 1);
                var scores = choice == request.Options.Count - 1 ? new Dictionary<int, double> { [choice] = -0.1 }
                    : new Dictionary<int, double> { [choice] = -0.1, [request.Options.Count - 1] = -2.5 };
                return new LlmDecision(choice, Scoreless ? new Dictionary<int, double>() : scores);
            }).ToList();
            return Task.FromResult(Result.Success(new LlmDecisions(DecideModel!, DecideDigest, "llm-choice", "1", "local", decisions)));
        }

        public bool On { get; set; } = true;

        public string? Digest { get; set; } = "sha256:fake";

        public string Answering { get; set; } = "fake-embed";

        public List<LlmEmbedRequest> Requests { get; } = [];

        public Task<string?> ModelAsync(CancellationToken ct) => Task.FromResult<string?>("fake-embed");

        public Task<bool> UseOnAsync(string use, CancellationToken ct) => Task.FromResult(use == ModelUses.Decide && On);

        public Task<string?> DigestAsync(string capability, string model, CancellationToken ct)
            => Task.FromResult(capability == ModelUses.Embed && model == "fake-embed" ? Digest
                : capability == ModelUses.Decide && model == DecideModel ? DecideDigest : null);

        public Task<Result<LlmEmbeddings>> EmbedAsync(LlmEmbedRequest request, CancellationToken ct)
        {
            lock (Requests)
                Requests.Add(request);
            return Task.FromResult(Result.Success(new LlmEmbeddings(Answering, 4, [.. request.Texts.Select(Vector)])));
        }

        private static IReadOnlyList<float> Vector(string text)
        {
            if (text.Contains("MARKET", StringComparison.Ordinal))
                return [1f, 0f, 0f, 0f];
            if (text.Contains("SHOP", StringComparison.Ordinal))
                return [0f, 0f, 1f, 0f];
            return text.Contains("GARAGE", StringComparison.Ordinal) || text.Contains("véhicule", StringComparison.Ordinal) ? [0f, 0f, 0f, 1f] : [0f, 1f, 0f, 0f];
        }
    }
}
