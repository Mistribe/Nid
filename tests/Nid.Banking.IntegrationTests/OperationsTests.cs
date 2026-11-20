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
}
