using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Host.IntegrationTests.Admin;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>5C spec §8: the bank line tools answer as their routes do, and a link is proposed, never written, by a propose token.</summary>
public class McpBankingToolTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class FixedClock : IClock
    {
        public Instant GetCurrentInstant() => new LocalDate(2026, 10, 29).At(new LocalTime(10, 0)).InUtc().ToInstant();
    }

    private static async Task<McpClient> ClientAsync(NidApp app, string preset)
        => await McpHarness.ClientAsync(app, (await TokenApi.CreateAsync(app, app.AdultA, preset)).Secret);

    private static async Task<(bool Error, string Text)> CallAsync(McpClient client, string tool, object args)
    {
        var result = await client.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!);
        return (result.IsError == true, McpHarness.TextOf(result));
    }

    private static async Task<string> ReadAsync(McpClient client, string tool, object args)
    {
        var (error, text) = await CallAsync(client, tool, args);
        Assert.False(error, text);
        return text;
    }

    private static void SameJson(string rest, string mcp)
        => Assert.Equal(JsonDocument.Parse(rest).RootElement.ToString(), JsonDocument.Parse(mcp).RootElement.ToString());

    private sealed record Setup(NidApp App, Guid Account, ItemId Envelope, ItemId Netflix, Guid Linked, Guid Open) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>A's « Courant » on an envelope, a Netflix subscription, two lines of it: one linked, one left open.</summary>
    private async Task<Setup> StartAsync()
    {
        var app = await BankingApi.StartAsync(fixture, overrides: s => s.AddSingleton<IClock>(new FixedClock()));
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant"));
        var account = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray().Single().GetProperty("id").GetGuid();
        var envelope = await app.NewEnvelopeAsync("Courant", app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{account}", new { envelopeId = envelope.Value })).EnsureSuccessStatusCode();
        var netflix = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{netflix.Value}/phases", new
        {
            startsOn = "2026-01-05", price = new { amount = "12.99", currency = "EUR" }, every = new { count = 1, unit = "month" }, payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        await store.AddAsync(account,
        [
            new NewBankTransaction("r-1", new LocalDate(2026, 9, 5), null, -12.99m, Currency.Eur, "PRLV SEPA NETFLIX 123", null, null, "api"),
            new NewBankTransaction("r-2", new LocalDate(2026, 10, 5), null, -12.99m, Currency.Eur, "VIR SEPA BOB", null, null, "api"),
        ], default);
        var lines = (await store.ListAsync([account], null, null, 0, 100, default)).ToDictionary(t => t.ProviderRef, t => t.Id);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/transactions/{lines["r-1"]}/link", new { module = "subscriptions", itemId = netflix.Value })).EnsureSuccessStatusCode();
        return new Setup(app, account, envelope, netflix, lines["r-1"], lines["r-2"]);
    }

    [Fact]
    public async Task list_bank_transactions_defaults_to_unlinked_with_decimal_strings()
    {
        await using var s = await StartAsync();
        await using var client = await ClientAsync(s.App, TokenPresets.Read);

        var unlinked = await ReadAsync(client, "list_bank_transactions", new { });
        SameJson(await s.App.GetRawAsync(s.App.AdultA, $"{BankingApi.Root}/transactions?state=unlinked"), unlinked);
        var items = JsonDocument.Parse(unlinked).RootElement.GetProperty("items");
        Assert.Equal(("VIR SEPA BOB", "-12.99"), (items.EnumerateArray().Single().GetProperty("label").GetString(), items[0].GetProperty("amount").GetString()));

        SameJson(await s.App.GetRawAsync(s.App.AdultA, $"{BankingApi.Root}/transactions?state=all&from=2026-10-01&to=2026-10-31"),
            await ReadAsync(client, "list_bank_transactions", new { state = "all", from = "2026-10-01", to = "2026-10-31" }));
        SameJson(await s.App.GetRawAsync(s.App.AdultA, $"{BankingApi.Root}/envelopes/{s.Envelope.Value}/transactions?state=linked"),
            await ReadAsync(client, "list_bank_transactions", new { envelopeId = s.Envelope.Value, state = "linked" }));
    }

    [Fact]
    public async Task list_expected_charges_answers_as_its_route()
    {
        await using var s = await StartAsync();
        await using var client = await ClientAsync(s.App, TokenPresets.Read);

        var charges = await ReadAsync(client, "list_expected_charges", new { from = "2026-10-01", to = "2026-12-31" });

        SameJson(await s.App.GetRawAsync(s.App.AdultA, $"{BankingApi.Root}/expected-charges?from=2026-10-01&to=2026-12-31"), charges);
        Assert.Equal(["2026-10-05", "2026-11-05", "2026-12-05"], JsonDocument.Parse(charges).RootElement.EnumerateArray().Select(c => c.GetProperty("dueOn").GetString()));
    }

    [Fact]
    public async Task propose_bank_link_submits_a_pending_link()
    {
        await using var s = await StartAsync();
        await using var client = await ClientAsync(s.App, TokenPresets.Propose);

        var answer = JsonDocument.Parse(await ReadAsync(client, "propose_bank_link",
            new { transactionId = s.Open, module = "subscriptions", itemId = s.Netflix.Value, sourceLabel = "Relevé octobre" })).RootElement;

        Assert.Equal(("proposed", "banking.link_transaction"), (answer.GetProperty("outcome").GetString(), answer.GetProperty("proposal").GetProperty("kind").GetString()));
        var line = JsonDocument.Parse(await s.App.GetRawAsync(s.App.AdultA, $"{BankingApi.Root}/transactions?state=unlinked")).RootElement.GetProperty("items");
        Assert.Equal(s.Open, line.EnumerateArray().Single().GetProperty("id").GetGuid()); // still open until an adult approves

        await using var read = await ClientAsync(s.App, TokenPresets.Read);
        Assert.True((await CallAsync(read, "propose_bank_link", new { transactionId = s.Open, module = "subscriptions", itemId = s.Netflix.Value })).Error);
    }

    private static async Task<Guid> AddLineAsync(NidApp app, Guid account, string reference, string label)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        await store.AddAsync(account, [new NewBankTransaction(reference, new LocalDate(2026, 10, 6), null, -4.50m, Currency.Eur, label, null, null, "api")], default);
        return (await store.ListAsync([account], null, null, 0, 100, default)).Single(t => t.ProviderRef == reference).Id;
    }

    private static async Task<Guid> AccountAsync(NidApp app, PersonId person, string name)
        => (await BankingApi.AccountsAsync(app, person)).EnumerateArray().Single(a => a.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

    [Fact]
    public async Task Uncategorised_lists_only_readable_lines()
    {
        await using var s = await StartAsync();
        var groceries = CategorySeed.Find("food.groceries")!.Id;
        var kept = await AddLineAsync(s.App, s.Account, "r-3", "CARREFOUR CITY");
        var ignored = await AddLineAsync(s.App, s.Account, "r-4", "AVOIR DIVERS");
        var done = await AddLineAsync(s.App, s.Account, "r-5", "BOULANGERIE");
        (await s.App.RawAsync(s.App.AdultA, HttpMethod.Post, $"{BankingApi.Root}/transactions/{ignored}/ignore", new { })).EnsureSuccessStatusCode();
        (await s.App.RawAsync(s.App.AdultA, HttpMethod.Put, $"{BankingApi.Root}/transactions/{done}/category", new { categoryId = groceries })).EnsureSuccessStatusCode();
        var none = await AddLineAsync(s.App, s.Account, "r-6", "SANS CATEGORIE");
        (await s.App.RawAsync(s.App.AdultA, HttpMethod.Put, $"{BankingApi.Root}/transactions/{none}/category", new { categoryId = (Guid?)null })).EnsureSuccessStatusCode();
        await BankingApi.ConnectAsync(s.App, s.App.AdultB, "code-b", BankingApi.Account("uid-b", "hash-b", null, "Courant B"));
        await AddLineAsync(s.App, await AccountAsync(s.App, s.App.AdultB, "Courant B"), "b-1", "LIGNE PRIVEE DE B");
        await using var client = await ClientAsync(s.App, TokenPresets.Read);

        var answer = JsonDocument.Parse(await ReadAsync(client, "list_uncategorised_bank_transactions", new { })).RootElement;

        Assert.Equal(["CARREFOUR CITY", "VIR SEPA BOB"], answer.GetProperty("items").EnumerateArray().Select(l => l.GetProperty("label").GetString()).Order());
        var line = answer.GetProperty("items").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == kept);
        Assert.Equal(("-4.50", "Courant"), (line.GetProperty("amount").GetString(), line.GetProperty("accountName").GetString()));
        var category = answer.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == groceries);
        Assert.Equal(("Courses", "Alimentation", "expense"), (category.GetProperty("name").GetString(), category.GetProperty("parentName").GetString(), category.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task Propose_creates_one_pending_proposal()
    {
        await using var s = await StartAsync();
        var groceries = CategorySeed.Find("food.groceries")!.Id;
        await using var client = await ClientAsync(s.App, TokenPresets.Propose);

        var answer = JsonDocument.Parse(await ReadAsync(client, "propose_bank_categories",
            new { lines = new[] { new { transactionId = s.Open, categoryId = groceries } } })).RootElement;

        Assert.Equal(("proposed", "banking.categorise_transactions"), (answer.GetProperty("outcome").GetString(), answer.GetProperty("proposal").GetProperty("kind").GetString()));
        var line = JsonDocument.Parse(await s.App.GetRawAsync(s.App.AdultA, $"{BankingApi.Root}/transactions?state=unlinked")).RootElement.GetProperty("items");
        Assert.Equal(JsonValueKind.Null, line.EnumerateArray().Single().GetProperty("category").ValueKind); // nothing changes until an adult approves

        await using var read = await ClientAsync(s.App, TokenPresets.Read);
        Assert.True((await CallAsync(read, "propose_bank_categories", new { lines = new[] { new { transactionId = s.Open, categoryId = groceries } } })).Error);

        // A line in a pending proposal is listed again, and the read tool says so.
        Assert.Contains(s.Open, JsonDocument.Parse(await ReadAsync(read, "list_uncategorised_bank_transactions", new { })).RootElement
            .GetProperty("items").EnumerateArray().Select(l => l.GetProperty("id").GetGuid()));
        Assert.Contains("already in a pending proposal may be listed again", (await read.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken))
            .Single(t => t.Name == "list_uncategorised_bank_transactions").Description);
    }

    [Fact]
    public async Task A_person_without_accounts_gets_an_empty_page()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant"));
        await AddLineAsync(app, await AccountAsync(app, app.AdultA, "Courant"), "a-1", "CARREFOUR CITY");
        await using var client = await McpHarness.ClientAsync(app, (await TokenApi.CreateAsync(app, app.AdultB, TokenPresets.Read)).Secret);

        var answer = JsonDocument.Parse(await ReadAsync(client, "list_uncategorised_bank_transactions", new { })).RootElement;

        Assert.Empty(answer.GetProperty("items").EnumerateArray());
        Assert.False(answer.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task get_spending_answers_as_its_route_and_is_empty_for_a_person_without_accounts()
    {
        await using var s = await StartAsync();
        await using var client = await ClientAsync(s.App, TokenPresets.Read);

        var month = await ReadAsync(client, "get_spending", new { month = "2026-10" });
        SameJson(await s.App.GetRawAsync(s.App.AdultA, $"{BankingApi.Root}/spending?month=2026-10"), month);
        var figures = JsonDocument.Parse(month).RootElement;
        Assert.Equal(("EUR", "12.99"), (figures.GetProperty("currency").GetString(), figures.GetProperty("total").GetString()));
        SameJson(await s.App.GetRawAsync(s.App.AdultA, $"{BankingApi.Root}/spending?scope=me"), await ReadAsync(client, "get_spending", new { scope = "me" }));

        // Bob reads no account of a household whose only account is Alice's own.
        await using var alone = await BankingApi.StartAsync(fixture, overrides: o => o.AddSingleton<IClock>(new FixedClock()));
        await BankingApi.ConnectAsync(alone, alone.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant"));
        await AddLineAsync(alone, await AccountAsync(alone, alone.AdultA, "Courant"), "a-1", "CARREFOUR CITY");
        await using var nobody = await McpHarness.ClientAsync(alone, (await TokenApi.CreateAsync(alone, alone.AdultB, TokenPresets.Read)).Secret);
        var empty = JsonDocument.Parse(await ReadAsync(nobody, "get_spending", new { month = "2026-10" })).RootElement;
        Assert.Equal("0.00", empty.GetProperty("total").GetString());
        Assert.Empty(empty.GetProperty("categories").EnumerateArray());
    }
}
