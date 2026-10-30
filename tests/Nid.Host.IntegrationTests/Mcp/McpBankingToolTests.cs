using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
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
}
