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

/// <summary>Transaction categories spec §Rules, decision 7: the rule routes, who sees and edits a rule, and the preview and apply of past lines.</summary>
public class CategoryRuleRouteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static string Root => $"{BankingApi.Root}/category-rules";

    private static Guid Seed(string key) => CategorySeed.Find(key)!.Id;

    private sealed record Setup(NidApp App, Guid Commun, Guid Livret) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>A's « Commun », on an envelope A and B own (both write it), and A's « Livret A », which B cannot read.</summary>
    private static async Task<Setup> StartAsync(PostgresFixture fixture)
    {
        var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Commun"), BankingApi.Account("uid-2", "hash-2", null, "Livret A", "SVGS"));
        var accounts = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray()
            .ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("id").GetGuid());
        var envelope = await app.NewEnvelopeAsync("Commun", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{accounts["Commun"]}",
            new { envelopeId = envelope.Value })).StatusCode);
        return new Setup(app, accounts["Commun"], accounts["Livret A"]);
    }

    private static async Task<Guid> AddAsync(NidApp app, Guid account, string reference, string label, string? counterparty = null, decimal amount = -30m)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        await store.AddAsync(account, [new NewBankTransaction(reference, new LocalDate(2026, 10, 5), null, amount, Currency.Eur, label, counterparty, null, "api")], default);
        return (await store.ListAsync([account], null, null, 0, 100, default)).Single(t => t.ProviderRef == reference).Id;
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        Assert.True(response.StatusCode == status, await response.Content.ReadAsStringAsync());
        return await BankingApi.JsonAsync(response);
    }

    private static Task<HttpResponseMessage> CreateAsync(NidApp app, PersonId person, object body) => app.RawAsync(person, HttpMethod.Post, Root, body);

    private static async Task<Guid> RuleAsync(NidApp app, PersonId person, string pattern, Guid? accountId = null, string key = "food.groceries")
        => (await OkAsync(await CreateAsync(app, person, new { pattern, accountId, categoryId = Seed(key) }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static async Task<List<JsonElement>> ListAsync(NidApp app, PersonId person)
        => [.. (await OkAsync(await app.RawAsync(person, HttpMethod.Get, Root))).EnumerateArray()];

    private static async Task<(int Total, List<Guid> Items)> PreviewAsync(NidApp app, PersonId person, Guid rule)
    {
        var preview = await OkAsync(await app.RawAsync(person, HttpMethod.Get, $"{Root}/{rule}/preview"));
        return (preview.GetProperty("total").GetInt32(), [.. preview.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())]);
    }

    private static async Task<int> ApplyAsync(NidApp app, PersonId person, Guid rule)
        => (await OkAsync(await app.RawAsync(person, HttpMethod.Post, $"{Root}/{rule}/apply"))).GetProperty("changed").GetInt32();

    [Fact]
    public async Task Restricted_rule_is_invisible_to_a_non_reader()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        await AddAsync(app, s.Livret, "l", "CARREFOUR CITY 12");
        await AddAsync(app, s.Commun, "c", "CARREFOUR CITY 34");
        var restricted = await RuleAsync(app, app.AdultA, "CARREFOUR", s.Livret);
        var everywhere = await RuleAsync(app, app.AdultA, "CITY", key: "food.restaurants");
        Assert.Equal(1, await ApplyAsync(app, app.AdultA, restricted));
        Assert.Equal(1, await ApplyAsync(app, app.AdultA, everywhere));

        var mine = (await ListAsync(app, app.AdultA)).ToDictionary(r => r.GetProperty("id").GetGuid());
        Assert.Equal(("Livret A", 1, true), (mine[restricted].GetProperty("accountName").GetString(), mine[restricted].GetProperty("lines").GetInt32(),
            mine[restricted].GetProperty("canEdit").GetBoolean()));
        var theirs = Assert.Single(await ListAsync(app, app.AdultB));
        Assert.Equal(everywhere, theirs.GetProperty("id").GetGuid());
        Assert.Equal(1, theirs.GetProperty("lines").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Root}/{restricted}/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Post, $"{Root}/{restricted}/apply")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Root}/{restricted}")).StatusCode);
    }

    [Fact]
    public async Task Preview_lists_only_lines_the_actor_writes_and_apply_changes_exactly_them()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var hidden = await AddAsync(app, s.Livret, "l", "CARREFOUR CITY 12");
        var byLabel = await AddAsync(app, s.Commun, "a", "CB CARREFOUR CITY 34");
        // The label says nothing; the counterparty, accented and spaced, does once normalised.
        var byCounterparty = await AddAsync(app, s.Commun, "b", "PAIEMENT 5678", "Carréfour  City");
        var byHand = await AddAsync(app, s.Commun, "c", "CARREFOUR CITY 56");
        await OkAsync(await app.RawAsync(app.AdultB, HttpMethod.Put, $"{BankingApi.Root}/transactions/{byHand}/category",
            new { categoryId = Seed("shopping.gifts") }));
        await AddAsync(app, s.Commun, "d", "BOULANGERIE");
        var rule = await RuleAsync(app, app.AdultB, "Carrefour City");

        var (total, items) = await PreviewAsync(app, app.AdultB, rule);
        Assert.Equal(2, total);
        Assert.Equal(new[] { byLabel, byCounterparty }.Order(), items.Order());

        Assert.Equal(2, await ApplyAsync(app, app.AdultB, rule));
        var after = await PreviewAsync(app, app.AdultB, rule);
        Assert.Equal(0, after.Total);
        Assert.Empty(after.Items);
        // A's own preview reaches her private line only: the shared ones took the category already.
        Assert.Equal([hidden], (await PreviewAsync(app, app.AdultA, rule)).Items);
        var lines = (await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{BankingApi.Root}/transactions?state=all")))
            .GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetGuid(), i => i.GetProperty("categorySource").GetString());
        Assert.Equal(("rule", "rule", "hand", (string?)null), (lines[byLabel], lines[byCounterparty], lines[byHand], lines[hidden]));
        Assert.NotNull((await ListAsync(app, app.AdultB)).Single().GetProperty("lastUsedAt").GetString());
    }

    [Theory]
    [InlineData("Cb")]
    [InlineData("12")]
    [InlineData(" x ")]
    public async Task Short_pattern_is_refused(string pattern)
    {
        await using var s = await StartAsync(fixture);
        await BankingApi.AssertRefusedAsync(await CreateAsync(s.App, s.App.AdultA, new { pattern, categoryId = Seed("food.groceries") }),
            HttpStatusCode.UnprocessableEntity, BankingCodes.RulePattern);
    }

    [Theory]
    [InlineData("50", "10")]
    [InlineData("-5", null)]
    [InlineData("abc", null)]
    [InlineData("1000000000000000", null)]
    public async Task Min_over_max_is_refused_with_rule_amounts(string? minAmount, string? maxAmount)
    {
        await using var s = await StartAsync(fixture);
        var response = await CreateAsync(s.App, s.App.AdultA, new { pattern = "NETFLIX", minAmount, maxAmount, categoryId = Seed("leisure.subscriptions") });
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(BankingCodes.RuleAmounts, body);
        Assert.Contains("minAmount", body);
    }

    [Fact]
    public async Task Bounds_and_direction_round_trip_as_strings()
    {
        await using var s = await StartAsync(fixture);
        var created = await OkAsync(await CreateAsync(s.App, s.App.AdultA,
            new { pattern = "prlv sepa netflix", direction = "debit", minAmount = "10", maxAmount = "20.5", categoryId = Seed("leisure.subscriptions") }),
            HttpStatusCode.Created);
        Assert.Equal(("NETFLIX", "debit", "10.00", "20.50"), (created.GetProperty("pattern").GetString(), created.GetProperty("direction").GetString(),
            created.GetProperty("minAmount").GetString(), created.GetProperty("maxAmount").GetString()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CreateAsync(s.App, s.App.AdultA,
            new { pattern = "NETFLIX", direction = "out", categoryId = Seed("leisure.subscriptions") })).StatusCode);
    }

    [Fact]
    public async Task Only_author_or_account_writer_edits()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var alices = await RuleAsync(app, app.AdultA, "NETFLIX", key: "leisure.subscriptions");
        var onCommun = await RuleAsync(app, app.AdultA, "CARREFOUR", s.Commun);
        var edit = new { pattern = "CARREFOUR MARKET", accountId = s.Commun, categoryId = Seed("food.groceries") };

        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Patch, $"{Root}/{alices}", edit)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Root}/{alices}")).StatusCode);
        var flags = (await ListAsync(app, app.AdultB)).ToDictionary(r => r.GetProperty("id").GetGuid(), r => r.GetProperty("canEdit").GetBoolean());
        Assert.Equal((false, true), (flags[alices], flags[onCommun]));

        var edited = await OkAsync(await app.RawAsync(app.AdultB, HttpMethod.Patch, $"{Root}/{onCommun}", edit));
        Assert.Equal("CARREFOUR MARKET", edited.GetProperty("pattern").GetString());
        // Moving a rule onto an account the editor cannot read: not found, never named.
        await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultB, HttpMethod.Patch, $"{Root}/{onCommun}", edit with { accountId = s.Livret }),
            HttpStatusCode.NotFound, BankingCodes.AccountNotFound);
        await BankingApi.AssertRefusedAsync(await CreateAsync(app, app.AdultB, edit with { accountId = s.Livret }),
            HttpStatusCode.NotFound, BankingCodes.AccountNotFound);
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Root}/{onCommun}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Root}/{alices}")).StatusCode);
    }
}
