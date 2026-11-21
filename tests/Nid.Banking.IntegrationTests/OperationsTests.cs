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

namespace Nid.Banking.IntegrationTests;

/// <summary>#84: one paged, filtered, searchable list of every line the reader reads, with its total, converted totals and facets.</summary>
public class OperationsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Route = $"{BankingApi.Root}/operations";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record World(NidApp App, Guid Joint, Guid PersonA, Guid PersonB);

    private static Guid Seed(string key) => CategorySeed.Find(key)!.Id;

    /// <summary>A joint account and Alice's own (Alice), Bob's private one (Bob): the lines are invented.</summary>
    private async Task<World> StartAsync()
    {
        var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Commun"), BankingApi.Account("uid-3", "hash-3", null, "Perso A"));
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-b", "hash-b", null, "Perso B"));
        var a = await SpendingTests.AccountsAsync(app, app.AdultA);
        var b = await SpendingTests.AccountsAsync(app, app.AdultB);
        await SpendingTests.PatchAsync(app, app.AdultA, a["Commun"], new { envelopeId = (await app.NewEnvelopeAsync("Commun", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000))).Value });
        await SpendingTests.PatchAsync(app, app.AdultA, a["Perso A"], new { envelopeId = (await app.NewEnvelopeAsync("Perso A", app.AdultA, Visibility.Private)).Value });
        await SpendingTests.PatchAsync(app, app.AdultB, b["Perso B"], new { envelopeId = (await app.NewEnvelopeAsync("Perso B", app.AdultB, Visibility.Private)).Value });
        return new World(app, a["Commun"], a["Perso A"], b["Perso B"]);
    }

    private static async Task<Guid> LineAsync(
        NidApp app, Guid account, LocalDate on, decimal amount, string label, string? counterparty = null, string? category = null, string currency = "EUR")
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        var reference = $"ref-{Guid.NewGuid():N}";
        Assert.True(Currency.TryCreate(currency, out var money));
        await store.AddAsync(account, [new NewBankTransaction(reference, on, null, amount, money, label, counterparty, null, "api")], Ct);
        var id = (await store.ListAsync([account], null, null, 0, 500, Ct)).Single(t => t.ProviderRef == reference).Id;
        if (category is not null)
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ICategoryStore>()
                .SetForLinesAsync([id], Seed(category), CategorySource.Hand, "person:test", Instant.FromUtc(2025, 10, 15, 12, 0), true, Ct));
        return id;
    }

    /// <summary>Eight lines of Alice and Bob's joint account and Alice's own, and one of Bob's private account.</summary>
    private static async Task SeedLinesAsync(World w)
    {
        await LineAsync(w.App, w.Joint, new(2025, 9, 10), -100m, "CB SUPERMARCHE", category: "food.groceries");
        await LineAsync(w.App, w.Joint, new(2025, 10, 3), -40m, "CARTE DENTISTE", "Cabinet Dentaire");
        await LineAsync(w.App, w.Joint, new(2025, 10, 5), 20m, "VIR REMBOURSEMENT", category: "income.refunds");
        await LineAsync(w.App, w.PersonA, new(2025, 10, 7), -10m, "CB CRÈCHE ÉTOILE", "Crèche Étoile", "food.restaurants");
        await LineAsync(w.App, w.PersonA, new(2025, 10, 8), -30m, "CB CARBURANT", category: "transport.fuel");
        await LineAsync(w.App, w.Joint, new(2025, 10, 9), -5m, "REMISE 50 POUR CENT");
        var transfer = await LineAsync(w.App, w.Joint, new(2025, 10, 11), -300m, "VIR EPARGNE");
        await using (var scope = w.App.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<ILinkStore>().SetAsync(
                new BankLink(transfer, null, null, null, w.PersonA, LinkOutcome.InternalTransfer, null, "job", Instant.FromUtc(2025, 10, 15, 12, 0)),
                TransactionLinkState.Internal, Ct);
        }
        var ignored = await LineAsync(w.App, w.Joint, new(2025, 10, 12), -70m, "CB ABANDONNE");
        Assert.Equal(HttpStatusCode.OK, (await w.App.RawAsync(w.App.AdultA, HttpMethod.Post, $"{BankingApi.Root}/transactions/{ignored}/ignore")).StatusCode);
        await LineAsync(w.App, w.PersonB, new(2025, 10, 4), -1000m, "CB SECRET", category: "leisure.culture");
    }

    private static async Task<JsonElement> GetAsync(NidApp app, PersonId person, string query = "")
    {
        var response = await app.RawAsync(person, HttpMethod.Get, $"{Route}{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await BankingApi.JsonAsync(response);
    }

    private static string[] Labels(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("label").GetString()!)];

    private static string[] Names(JsonElement list) => [.. list.EnumerateArray().Select(o => o.GetProperty("name").GetString()!)];

    [Fact]
    public async Task Bob_private_lines_never_reach_Alice()
    {
        var w = await StartAsync();
        await using var _ = w.App;
        await SeedLinesAsync(w);

        var alice = await GetAsync(w.App, w.App.AdultA);
        Assert.Equal(8, alice.GetProperty("total").GetInt32());
        Assert.DoesNotContain("CB SECRET", Labels(alice));
        Assert.Equal("185.00", alice.GetProperty("totals").GetProperty("out").GetString());
        Assert.Equal("20.00", alice.GetProperty("totals").GetProperty("in").GetString());
        Assert.Equal("-165.00", alice.GetProperty("totals").GetProperty("net").GetString());
        Assert.Equal("EUR", alice.GetProperty("totals").GetProperty("currency").GetString());
        Assert.False(alice.GetProperty("totals").GetProperty("incomplete").GetBoolean());
        var facets = alice.GetProperty("facets");
        Assert.Equal(new[] { "Commun", "Perso A" }, Names(facets.GetProperty("accounts")).Order());
        Assert.DoesNotContain("Perso B", Names(facets.GetProperty("accounts")));
        Assert.Equal(2, facets.GetProperty("owners").GetArrayLength());
        // The owner column: the joint account has both adults, Alice's own her alone, and Bob's private one is not listed at all.
        var owned = alice.GetProperty("accountOwners").EnumerateArray().ToDictionary(o => o.GetProperty("accountId").GetGuid(), o => o.GetProperty("owners").GetArrayLength());
        Assert.Equal(2, owned.Count);
        Assert.Equal([1, 2], owned.Values.Order());
        Assert.DoesNotContain(w.PersonB, owned.Keys);
        // Bob's category is nobody else's: Alice's facet does not name it.
        Assert.DoesNotContain(Seed("leisure.culture"), facets.GetProperty("categories").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()));
        // Naming Bob's account, or his category's lines, reveals nothing.
        var asked = await GetAsync(w.App, w.App.AdultA, $"?account={w.PersonB}");
        Assert.Equal(0, asked.GetProperty("total").GetInt32());
        Assert.Empty(Labels(asked));

        var bob = await GetAsync(w.App, w.App.AdultB);
        Assert.Equal(7, bob.GetProperty("total").GetInt32());
        Assert.Contains("CB SECRET", Labels(bob));
        Assert.DoesNotContain("CB CARBURANT", Labels(bob));
    }

    [Theory]
    [InlineData("", 8)]
    [InlineData("?account={Joint}", 6)]
    [InlineData("?account={PersonA}&account={Joint}", 8)]
    [InlineData("?owner={B}", 6)]
    [InlineData("?owner={A}", 8)]
    [InlineData("?from=2025-10-05", 6)]
    [InlineData("?to=2025-09-30", 1)]
    [InlineData("?from=2025-10-05&to=2025-10-08", 3)]
    [InlineData("?category={Groceries}", 1)]
    [InlineData("?category={Food}", 2)]
    [InlineData("?uncategorised=true", 4)]
    [InlineData("?category={Groceries}&uncategorised=true", 5)]
    [InlineData("?state=all", 8)]
    [InlineData("?state=internal", 1)]
    [InlineData("?state=ignored", 1)]
    [InlineData("?state=unlinked", 6)]
    [InlineData("?state=counted", 6)]
    [InlineData("?scope=me", 8)]
    [InlineData("?scope=household", 6)]
    [InlineData("?scope=household&state=counted", 4)]
    [InlineData("?state=linked", 0)]
    [InlineData("?direction=in", 1)]
    [InlineData("?direction=out", 7)]
    [InlineData("?min=40", 4)]
    [InlineData("?max=10", 2)]
    [InlineData("?min=30&max=50", 2)]
    [InlineData("?q=dentiste", 1)]
    public async Task Each_filter_narrows_items_and_total(string query, int expected)
    {
        var w = await StartAsync();
        await using var _ = w.App;
        await SeedLinesAsync(w);
        query = query.Replace("{Joint}", w.Joint.ToString()).Replace("{PersonA}", w.PersonA.ToString())
            .Replace("{A}", w.App.AdultA.Value.ToString()).Replace("{B}", w.App.AdultB.Value.ToString())
            .Replace("{Groceries}", Seed("food.groceries").ToString()).Replace("{Food}", Seed("food").ToString());

        var page = await GetAsync(w.App, w.App.AdultA, query);

        Assert.Equal(expected, page.GetProperty("total").GetInt32());
        Assert.Equal(expected, page.GetProperty("items").GetArrayLength());
    }

    /// <summary>
    /// #437: a figure of Dépenses opens Opérations on its scope's accounts and the lines it counts: the same lines, the same total, an ignored
    /// and an internal line of the category left out, and Alice's own account only in « Moi ».
    /// </summary>
    [Theory]
    [InlineData("me", 3, "140.00")]
    [InlineData("household", 2, "130.00")]
    public async Task A_figure_link_lists_the_lines_behind_the_figure(string scope, int count, string figure)
    {
        var w = await StartAsync();
        await using var _ = w.App;
        await SeedLinesAsync(w);
        await LineAsync(w.App, w.Joint, new(2025, 10, 13), -30m, "CB EPICERIE", category: "food.groceries");
        var ignored = await LineAsync(w.App, w.Joint, new(2025, 10, 14), -60m, "CB EPICERIE ANNULEE", category: "food.groceries");
        Assert.Equal(HttpStatusCode.OK, (await w.App.RawAsync(w.App.AdultA, HttpMethod.Post, $"{BankingApi.Root}/transactions/{ignored}/ignore")).StatusCode);
        var transfer = await LineAsync(w.App, w.Joint, new(2025, 10, 15), -25m, "VIR COURSES", category: "food.groceries");
        await using (var services = w.App.Services.CreateAsyncScope())
        {
            services.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await services.ServiceProvider.GetRequiredService<ILinkStore>().SetAsync(
                new BankLink(transfer, null, null, null, w.PersonA, LinkOutcome.InternalTransfer, null, "job", Instant.FromUtc(2025, 10, 15, 12, 0)),
                TransactionLinkState.Internal, Ct);
        }
        var food = Seed("food");

        var response = await w.App.RawAsync(w.App.AdultA, HttpMethod.Get,
            $"{BankingApi.Root}/spending/lines?scope={scope}&categoryId={food}&from=2025-09&to=2025-10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = await BankingApi.JsonAsync(response);
        Assert.Equal((figure, count), (lines.GetProperty("total").GetString(), lines.GetProperty("count").GetInt32()));

        var list = await GetAsync(w.App, w.App.AdultA, $"?scope={scope}&state=counted&category={food}&from=2025-09-01&to=2025-10-31");
        Assert.Equal(count, list.GetProperty("total").GetInt32());
        Assert.Equal("-" + figure, list.GetProperty("totals").GetProperty("net").GetString());
        Assert.Equal(lines.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Order(),
            list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Order());
        // Without them, today's list: every account read, the ignored and the internal lines too.
        Assert.Equal(5, (await GetAsync(w.App, w.App.AdultA, $"?category={food}&from=2025-09-01&to=2025-10-31")).GetProperty("total").GetInt32());
    }

    /// <summary>
    /// #446: a line split in two parts of a category (and one elsewhere) is one row of the category's lines, as in Opérations: the same count and
    /// total for the same month and scope; the part filed elsewhere still shows in its own category's lines.
    /// </summary>
    [Fact]
    public async Task A_line_split_within_a_category_is_one_row_of_its_lines()
    {
        var w = await StartAsync();
        await using var _ = w.App;
        await SeedLinesAsync(w);
        var split = await LineAsync(w.App, w.Joint, new(2025, 10, 12), -90m, "CB HYPERMARCHE");
        var put = await w.App.RawAsync(w.App.AdultA, HttpMethod.Put, $"{BankingApi.Root}/transactions/{split}/split", new
        {
            parts = new object[]
            {
                new { amount = "-50", categoryId = Seed("food.groceries") }, new { amount = "-30", categoryId = Seed("food.restaurants") },
                new { amount = "-10", categoryId = Seed("transport.fuel") },
            },
        });
        Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync(Ct));
        var food = Seed("food");

        var response = await w.App.RawAsync(w.App.AdultA, HttpMethod.Get, $"{BankingApi.Root}/spending/lines?scope=household&categoryId={food}&month=2025-10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = await BankingApi.JsonAsync(response);
        var list = await GetAsync(w.App, w.App.AdultA, $"?scope=household&state=counted&category={food}&from=2025-10-01&to=2025-10-31");
        Assert.Equal(list.GetProperty("total").GetInt32(), lines.GetProperty("count").GetInt32());
        Assert.Equal(list.GetProperty("totals").GetProperty("net").GetString(), "-" + lines.GetProperty("total").GetString());
        Assert.Equal(list.GetProperty("items").GetArrayLength(), lines.GetProperty("items").GetArrayLength());
        var row = Assert.Single(lines.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == split);
        Assert.Equal("-80.00", row.GetProperty("amount").GetString());
        Assert.Equal(["-50.00", "-30.00"], row.GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("amount").GetString()!));

        var fuel = await BankingApi.JsonAsync(await w.App.RawAsync(w.App.AdultA, HttpMethod.Get,
            $"{BankingApi.Root}/spending/lines?scope=household&categoryId={Seed("transport")}&month=2025-10"));
        var part = Assert.Single(fuel.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == split);
        Assert.Equal("-10.00", part.GetProperty("amount").GetString());
    }

    /// <summary>
    /// #445: opened from a figure, the filter offers the accounts, owners and categories of the scope's accounts only: in « Moi », not Léa's
    /// account that Bob connected (Alice reads it, only Léa holds it); in « Foyer », not Alice's own account nor its categories.
    /// </summary>
    [Fact]
    public async Task A_scope_bounds_the_filter_options()
    {
        var w = await StartAsync();
        await using var _ = w.App;
        await SeedLinesAsync(w);
        await BankingApi.ConnectAsync(w.App, w.App.AdultB, "code-l", BankingApi.Account("uid-l", "hash-l", null, "Compte de Léa"));
        var lea = (await SpendingTests.AccountsAsync(w.App, w.App.AdultB))["Compte de Léa"];
        await SpendingTests.PatchAsync(w.App, w.App.AdultB, lea, new { envelopeId = (await w.App.NewEnvelopeAsync("Léa", Visibility.Shared, (w.App.Dependent, 10_000))).Value });
        await LineAsync(w.App, lea, new(2025, 10, 6), -15m, "CB PISCINE", category: "children.school");
        Guid[] Categories(JsonElement page) => [.. page.GetProperty("facets").GetProperty("categories").EnumerateArray().Select(c => c.GetProperty("id").GetGuid())];

        var everything = await GetAsync(w.App, w.App.AdultA);
        Assert.Equal(new[] { "Commun", "Compte de Léa", "Perso A" }, Names(everything.GetProperty("facets").GetProperty("accounts")).Order());
        Assert.Contains(Seed("children.school"), Categories(everything));
        Assert.Contains(Seed("transport.fuel"), Categories(everything));

        var me = await GetAsync(w.App, w.App.AdultA, "?scope=me&state=counted");
        Assert.Equal(new[] { "Commun", "Perso A" }, Names(me.GetProperty("facets").GetProperty("accounts")).Order());
        Assert.DoesNotContain(Seed("children.school"), Categories(me));
        Assert.DoesNotContain(Seed("children"), Categories(me));
        Assert.Contains(Seed("transport.fuel"), Categories(me));
        Assert.Equal(2, me.GetProperty("facets").GetProperty("owners").GetArrayLength());

        var household = await GetAsync(w.App, w.App.AdultA, "?scope=household&state=counted");
        Assert.Equal(new[] { "Commun", "Compte de Léa" }, Names(household.GetProperty("facets").GetProperty("accounts")).Order());
        Assert.DoesNotContain(Seed("transport.fuel"), Categories(household));
        Assert.Contains(Seed("children.school"), Categories(household));
    }

    [Fact]
    public async Task Search_is_case_and_accent_insensitive_and_literal()
    {
        var w = await StartAsync();
        await using var _ = w.App;
        await SeedLinesAsync(w);

        foreach (var q in new[] { "dentiste", "DENTISTE", "Dentiste" })
            Assert.Equal(["CARTE DENTISTE"], Labels(await GetAsync(w.App, w.App.AdultA, $"?q={q}")));
        foreach (var q in new[] { "creche", "crèche", "ETOILE", "étoile" })
            Assert.Equal(["CB CRÈCHE ÉTOILE"], Labels(await GetAsync(w.App, w.App.AdultA, $"?q={Uri.EscapeDataString(q)}")));
        // The counterparty too.
        Assert.Equal(["CARTE DENTISTE"], Labels(await GetAsync(w.App, w.App.AdultA, "?q=cabinet")));
        // « 50 » finds the line that holds it; « 50%_ » is literal text, found nowhere, and never an error.
        Assert.Equal(["REMISE 50 POUR CENT"], Labels(await GetAsync(w.App, w.App.AdultA, "?q=50")));
        Assert.Empty(Labels(await GetAsync(w.App, w.App.AdultA, $"?q={Uri.EscapeDataString("50%_")}")));
        Assert.Empty(Labels(await GetAsync(w.App, w.App.AdultA, $"?q={Uri.EscapeDataString("5\\")}")));
        // Bob's private line is not found by its text.
        Assert.Empty(Labels(await GetAsync(w.App, w.App.AdultA, "?q=secret")));
    }

    [Fact]
    public async Task A_parent_category_includes_its_children()
    {
        var w = await StartAsync();
        await using var _ = w.App;
        await LineAsync(w.App, w.Joint, new(2025, 10, 1), -10m, "SUR LE PARENT", category: "food");
        await LineAsync(w.App, w.Joint, new(2025, 10, 2), -10m, "SUR UN ENFANT", category: "food.groceries");
        await LineAsync(w.App, w.Joint, new(2025, 10, 3), -10m, "AILLEURS", category: "transport.fuel");

        Assert.Equal(new[] { "SUR LE PARENT", "SUR UN ENFANT" }, Labels(await GetAsync(w.App, w.App.AdultA, $"?category={Seed("food")}")).Order());
        Assert.Equal(["SUR UN ENFANT"], Labels(await GetAsync(w.App, w.App.AdultA, $"?category={Seed("food.groceries")}")));
        var facets = (await GetAsync(w.App, w.App.AdultA)).GetProperty("facets").GetProperty("categories");
        Assert.Contains("Alimentation", Names(facets));
    }

    [Fact]
    public async Task Totals_leave_out_internal_and_ignored_lines()
    {
        var w = await StartAsync();
        await using var _ = w.App;
        await SeedLinesAsync(w);

        // The 300 transfer and the 70 ignored line are listed, and counted in neither out nor in.
        var page = await GetAsync(w.App, w.App.AdultA, "?from=2025-10-11&to=2025-10-12");
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.Equal("0.00", page.GetProperty("totals").GetProperty("out").GetString());
        Assert.Equal("0.00", page.GetProperty("totals").GetProperty("in").GetString());
        Assert.Equal("0.00", page.GetProperty("totals").GetProperty("net").GetString());
    }

    [Fact]
    public async Task Totals_are_converted_and_flagged_without_a_rate()
    {
        var w = await StartAsync();
        await using var _ = w.App;
        await LineAsync(w.App, w.Joint, new(2025, 9, 10), -100m, "ACHAT EN DOLLARS", currency: "USD");
        await LineAsync(w.App, w.Joint, new(2025, 10, 5), -50m, "AUTRE ACHAT EN DOLLARS", currency: "USD");
        await LineAsync(w.App, w.Joint, new(2025, 10, 6), -10m, "ACHAT EN EUROS");

        var without = (await GetAsync(w.App, w.App.AdultA)).GetProperty("totals");
        Assert.True(without.GetProperty("incomplete").GetBoolean());
        Assert.Equal("10.00", without.GetProperty("out").GetString());

        await using (var scope = w.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IFxRateStore>().WriteAsync([
                new FxRate(new LocalDate(2025, 9, 30), Currency.Eur, 0.9m, FxRateSource.Ecb),
                new FxRate(new LocalDate(2025, 10, 1), Currency.Eur, 0.5m, FxRateSource.Ecb),
                new FxRate(new LocalDate(2025, 10, 15), Currency.Eur, 0.8m, FxRateSource.Ecb),
            ], Ct);
        // September at its month-end rate (90), October at today's (40), plus the 10 euros.
        var with = (await GetAsync(w.App, w.App.AdultA)).GetProperty("totals");
        Assert.False(with.GetProperty("incomplete").GetBoolean());
        Assert.Equal("140.00", with.GetProperty("out").GetString());
        Assert.Equal("-140.00", with.GetProperty("net").GetString());
    }

    [Fact]
    public async Task Page_two_holds_the_rest()
    {
        var w = await StartAsync();
        await using var _ = w.App;
        for (var i = 0; i < 60; i++)
            await LineAsync(w.App, w.Joint, new LocalDate(2025, 8, 1).PlusDays(i), -1m, $"LIGNE {i:00}");

        var first = await GetAsync(w.App, w.App.AdultA);
        var second = await GetAsync(w.App, w.App.AdultA, "?page=2");

        Assert.Equal(60, first.GetProperty("total").GetInt32());
        Assert.Equal(60, second.GetProperty("total").GetInt32());
        Assert.Equal(50, first.GetProperty("items").GetArrayLength());
        Assert.Equal(10, second.GetProperty("items").GetArrayLength());
        // Newest first: the page one starts with the latest booking, the page two ends with the oldest.
        Assert.Equal("LIGNE 59", Labels(first)[0]);
        Assert.Equal("LIGNE 00", Labels(second)[^1]);
        // The totals are the filter's, not the page's.
        Assert.Equal("60.00", first.GetProperty("totals").GetProperty("out").GetString());
    }

    [Theory]
    [InlineData("?state=bogus", "state")]
    [InlineData("?direction=sideways", "direction")]
    [InlineData("?min=20&max=10", "min")]
    [InlineData("?min=-1", "min")]
    [InlineData("?max=-1", "max")]
    [InlineData("?from=2025-10-05&to=2025-10-01", "from")]
    [InlineData("?from=yesterday", "from")]
    [InlineData("?q=a", "q")]
    [InlineData("?account=nope", "account")]
    [InlineData("?owner=nope", "owner")]
    [InlineData("?category=nope", "category")]
    [InlineData("?min=lots", "min")]
    [InlineData("?max=lots", "max")]
    [InlineData("?min=1,000", "min")]
    public async Task Bad_filters_are_field_errors(string query, string field)
    {
        var w = await StartAsync();
        await using var _ = w.App;

        var response = await w.App.RawAsync(w.App.AdultA, HttpMethod.Get, $"{Route}{query}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var errors = (await BankingApi.JsonAsync(response)).GetProperty("errors").EnumerateArray().ToList();
        Assert.Contains(field, errors.Select(e => e.FieldPath()));
        if (field is "account" or "owner" or "category" or "min" or "max")
            Assert.All(errors, e => Assert.Equal(Nid.Platform.Contracts.FieldErrors.ShapeCode, e.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task A_transfer_to_an_account_the_reader_cannot_read_counts_like_a_linked_line()
    {
        var w = await StartAsync();
        await using var _ = w.App;
        var line = await LineAsync(w.App, w.Joint, new(2025, 10, 3), -60m, "VIR VERS COMPTE DE BOB");
        await using (var scope = w.App.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<ILinkStore>().SetAsync(
                new BankLink(line, null, null, null, w.PersonB, LinkOutcome.InternalTransfer, null, "job", Instant.FromUtc(2025, 10, 15, 12, 0)),
                TransactionLinkState.Internal, Ct);
        }

        // Alice cannot read Bob's account: the line shows as linked, and its amount counts.
        var alice = await GetAsync(w.App, w.App.AdultA, "?state=linked");
        Assert.Equal(1, alice.GetProperty("total").GetInt32());
        Assert.Equal("60.00", alice.GetProperty("totals").GetProperty("out").GetString());
        Assert.Equal("60.00", (await GetAsync(w.App, w.App.AdultA)).GetProperty("totals").GetProperty("out").GetString());
        // Bob reads both accounts: for him it is a transfer, and it counts in neither.
        Assert.Equal("0.00", (await GetAsync(w.App, w.App.AdultB, "?state=internal")).GetProperty("totals").GetProperty("out").GetString());
    }

    /// <summary>A −110 line split 40 « Courses » / 70 « Restauration », a −10 « Courses » line, a −20 line with no category.</summary>
    private static async Task SeedSplitAsync(World w)
    {
        var split = await LineAsync(w.App, w.Joint, new(2025, 10, 3), -110m, "CB SUPERMARCHE ET RESTO");
        await LineAsync(w.App, w.Joint, new(2025, 10, 4), -10m, "CB MARCHE", category: "food.groceries");
        await LineAsync(w.App, w.Joint, new(2025, 10, 5), -20m, "CB DIVERS");
        var response = await w.App.RawAsync(w.App.AdultA, HttpMethod.Put, $"{BankingApi.Root}/transactions/{split}/split", new
        {
            parts = new object[]
            {
                new { amount = "-40", categoryId = Seed("food.groceries") },
                new { amount = "-70", categoryId = Seed("food.restaurants") },
            },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Totals_under_a_category_filter_count_the_matching_parts_of_a_split_line()
    {
        var w = await StartAsync();
        await using var _ = w.App;
        await SeedSplitAsync(w);

        var groceries = await GetAsync(w.App, w.App.AdultA, $"?category={Seed("food.groceries")}");
        Assert.Equal(2, groceries.GetProperty("total").GetInt32());
        Assert.Equal("50.00", groceries.GetProperty("totals").GetProperty("out").GetString());
        Assert.Equal("70.00", (await GetAsync(w.App, w.App.AdultA, $"?category={Seed("food.restaurants")}")).GetProperty("totals").GetProperty("out").GetString());
        // No category filter: the lines are summed.
        Assert.Equal("140.00", (await GetAsync(w.App, w.App.AdultA)).GetProperty("totals").GetProperty("out").GetString());
    }

    [Fact]
    public async Task Totals_of_uncategorised_count_an_uncategorised_part_not_the_whole_split_line()
    {
        var w = await StartAsync();
        await using var _ = w.App;
        var split = await LineAsync(w.App, w.Joint, new(2025, 10, 3), -110m, "CB MIXTE");
        await LineAsync(w.App, w.Joint, new(2025, 10, 5), -20m, "CB DIVERS");
        var response = await w.App.RawAsync(w.App.AdultA, HttpMethod.Put, $"{BankingApi.Root}/transactions/{split}/split", new
        {
            parts = new object[] { new { amount = "-40", categoryId = Seed("food.groceries") }, new { amount = "-70" } },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await GetAsync(w.App, w.App.AdultA, "?uncategorised=true");
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.Equal("90.00", page.GetProperty("totals").GetProperty("out").GetString());
    }

    [Fact]
    public async Task An_account_a_reader_cannot_write_says_so()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Lecture"), BankingApi.Account("uid-2", "hash-2", null, "Commun"));
        var accounts = await SpendingTests.AccountsAsync(app, app.AdultA);
        // Alice owns the first envelope and Bob only reads it; the second is written by both.
        await SpendingTests.PatchAsync(app, app.AdultA, accounts["Lecture"], new { envelopeId = (await app.NewEnvelopeAsync("Lecture", app.AdultA, Visibility.Shared)).Value });
        await SpendingTests.PatchAsync(app, app.AdultA, accounts["Commun"],
            new { envelopeId = (await app.NewEnvelopeAsync("Commun", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000))).Value });

        bool Writable(JsonElement page, string name) => page.GetProperty("accountOwners").EnumerateArray()
            .Single(o => o.GetProperty("accountId").GetGuid() == accounts[name]).GetProperty("writable").GetBoolean();
        var bob = await GetAsync(app, app.AdultB);
        Assert.False(Writable(bob, "Lecture"));
        Assert.True(Writable(bob, "Commun"));
        Assert.True(Writable(await GetAsync(app, app.AdultA), "Lecture"));
    }
}
