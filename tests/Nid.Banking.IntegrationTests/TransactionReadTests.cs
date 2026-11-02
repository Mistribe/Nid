using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Application.Sync;
using Nid.Banking.Contracts;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.IntegrationTests;

/// <summary>Bank sync spec §6 and D3: an envelope's transactions read page by page, and kept as long as the household chose.</summary>
public class TransactionReadTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class SettableClock(Instant now) : IClock
    {
        public Instant Now { get; set; } = now;

        public Instant GetCurrentInstant() => Now;
    }

    private static LocalDate D(int year, int month, int day) => new(year, month, day);

    private static Instant At(LocalDate day) => day.At(new LocalTime(10, 0)).InUtc().ToInstant();

    private static BankingDbContext Db(NidApp app) => new(new DbContextOptionsBuilder<BankingDbContext>().Configure(app.ConnectionString).Options);

    private static NewBankTransaction Line(string reference, LocalDate on, decimal amount, string label = "PRLV SEPA NETFLIX")
        => new(reference, on, null, amount, Currency.Eur, label, null, null, "api");

    private static async Task<(NidApp App, SettableClock Clock)> StartAsync(PostgresFixture fixture)
    {
        var clock = new SettableClock(At(D(2026, 10, 29)));
        return (await BankingApi.StartAsync(fixture, overrides: s => s.AddSingleton<IClock>(clock)), clock);
    }

    private static async Task AddAsync(NidApp app, Guid account, params NewBankTransaction[] lines)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<ITransactionStore>().AddAsync(account, lines, default);
    }

    /// <summary>A's « Courant » and her card, associated with a new envelope of hers; the ids of the three.</summary>
    private static async Task<(ItemId Envelope, Guid Account, Guid Card)> SetUpAsync(NidApp app, Visibility visibility = Visibility.Private)
    {
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Courant A"), BankingApi.Account("uid-2", "hash-2", null, "Carte A", "CARD"));
        var accounts = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("id").GetGuid());
        var envelope = await app.NewEnvelopeAsync("Courant", app.AdultA, visibility);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{accounts["Courant A"]}", new { envelopeId = envelope.Value })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{accounts["Carte A"]}", new { cardOfAccountId = accounts["Courant A"] })).StatusCode);
        return (envelope, accounts["Courant A"], accounts["Carte A"]);
    }

    private static async Task<JsonElement> ReadAsync(NidApp app, PersonId person, ItemId envelope, string query = "")
    {
        var response = await app.RawAsync(person, HttpMethod.Get, $"{BankingApi.Root}/envelopes/{envelope.Value}/transactions{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await BankingApi.JsonAsync(response);
    }

    private static string[] Labels(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("label").GetString()!)];

    [Fact]
    public async Task The_envelope_s_transactions_come_newest_first_with_the_card_s_lines_flagged()
    {
        var (app, _) = await StartAsync(fixture);
        await using var _app = app;
        var (envelope, account, card) = await SetUpAsync(app);
        await AddAsync(app, account, Line("a", D(2026, 10, 1), -10m, "LOYER"), Line("c", D(2026, 10, 3), 1500m, "VIR ACME"));
        await AddAsync(app, card, Line("b", D(2026, 10, 2), -20.5m, "CARTE AMAZON"));

        var page = await ReadAsync(app, app.AdultA, envelope);

        Assert.False(page.GetProperty("hasMore").GetBoolean());
        Assert.Equal(["VIR ACME", "CARTE AMAZON", "LOYER"], Labels(page));
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["1500.00", "-20.50", "-10.00"], items.Select(i => i.GetProperty("amount").GetString()));
        Assert.Equal([false, true, false], items.Select(i => i.GetProperty("fromCard").GetBoolean()));
        Assert.Equal([null, "Carte A", null], items.Select(i => i.GetProperty("cardName").GetString()));
        Assert.Equal(("2026-10-03", "EUR", "api"), (items[0].GetProperty("bookedOn").GetString(), items[0].GetProperty("currency").GetString(), items[0].GetProperty("fetchedVia").GetString()));
    }

    [Fact]
    public async Task Fifty_lines_make_a_page_and_the_next_page_follows()
    {
        var (app, _) = await StartAsync(fixture);
        await using var _app = app;
        var (envelope, account, _) = await SetUpAsync(app);
        await AddAsync(app, account, [.. Enumerable.Range(0, 51).Select(i => Line($"r{i}", D(2026, 9, 1).PlusDays(i % 28), -i - 1m, $"L{i}"))]);

        var first = await ReadAsync(app, app.AdultA, envelope);
        var second = await ReadAsync(app, app.AdultA, envelope, "?page=2");

        Assert.Equal((50, true), (first.GetProperty("items").GetArrayLength(), first.GetProperty("hasMore").GetBoolean()));
        Assert.Equal((1, false), (second.GetProperty("items").GetArrayLength(), second.GetProperty("hasMore").GetBoolean()));
        Assert.Equal(51, Labels(first).Concat(Labels(second)).Distinct().Count());
    }

    [Fact]
    public async Task From_and_to_filter_on_the_booking_date()
    {
        var (app, _) = await StartAsync(fixture);
        await using var _app = app;
        var (envelope, account, _) = await SetUpAsync(app);
        await AddAsync(app, account, Line("a", D(2026, 10, 1), -1m, "A"), Line("b", D(2026, 10, 10), -2m, "B"), Line("c", D(2026, 10, 20), -3m, "C"));

        Assert.Equal(["B", "A"], Labels(await ReadAsync(app, app.AdultA, envelope, "?to=2026-10-10")));
        Assert.Equal(["C", "B"], Labels(await ReadAsync(app, app.AdultA, envelope, "?from=2026-10-10")));
        Assert.Equal(["B"], Labels(await ReadAsync(app, app.AdultA, envelope, "?from=2026-10-02&to=2026-10-19")));
    }

    [Fact]
    public async Task Transactions_follow_the_account_access()
    {
        var (app, _) = await StartAsync(fixture);
        await using var _app = app;
        var (privateEnvelope, account, _) = await SetUpAsync(app);
        await AddAsync(app, account, Line("a", D(2026, 10, 1), -1m, "SECRET"));

        var refused = await app.RawAsync(app.AdultB, HttpMethod.Get, $"{BankingApi.Root}/envelopes/{privateEnvelope.Value}/transactions");
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.DoesNotContain("SECRET", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["SECRET"], Labels(await ReadAsync(app, app.AdultA, privateEnvelope)));

        // On a shared envelope, B reads what A's account holds.
        var shared = await app.NewEnvelopeAsync("Commun", app.AdultA, Visibility.Shared);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{account}", new { envelopeId = shared.Value })).StatusCode);
        Assert.Equal(["SECRET"], Labels(await ReadAsync(app, app.AdultB, shared)));
    }

    [Fact]
    public async Task A_card_s_lines_follow_its_checking_account_s_access()
    {
        var (app, _) = await StartAsync(fixture);
        await using var _app = app;
        var (privateEnvelope, account, card) = await SetUpAsync(app);
        await AddAsync(app, card, Line("c", D(2026, 10, 2), -20.5m, "CARTE SECRETE"));

        var refused = await app.RawAsync(app.AdultB, HttpMethod.Get, $"{BankingApi.Root}/envelopes/{privateEnvelope.Value}/transactions");
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.DoesNotContain("CARTE SECRETE", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var shared = await app.NewEnvelopeAsync("Commun", app.AdultA, Visibility.Shared);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{account}", new { envelopeId = shared.Value })).StatusCode);
        Assert.Equal(["CARTE SECRETE"], Labels(await ReadAsync(app, app.AdultB, shared)));
    }

    [Fact]
    public async Task A_page_past_the_last_is_empty_however_large()
    {
        var (app, _) = await StartAsync(fixture);
        await using var _app = app;
        var (envelope, account, _) = await SetUpAsync(app);
        await AddAsync(app, account, Line("a", D(2026, 10, 1), -1m, "A"));

        var page = await ReadAsync(app, app.AdultA, envelope, $"?page={int.MaxValue}");

        Assert.Equal((0, false), (page.GetProperty("items").GetArrayLength(), page.GetProperty("hasMore").GetBoolean()));
        Assert.Equal(["A"], Labels(await ReadAsync(app, app.AdultA, envelope, "?page=-5")));
    }

    [Fact]
    public async Task An_envelope_without_a_bank_account_has_an_empty_page()
    {
        var (app, _) = await StartAsync(fixture);
        await using var _app = app;
        var envelope = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Private);

        var page = await ReadAsync(app, app.AdultA, envelope);

        Assert.Equal((0, false), (page.GetProperty("items").GetArrayLength(), page.GetProperty("hasMore").GetBoolean()));
    }

    [Fact]
    public async Task Reading_transactions_needs_banking_on_but_no_enable_banking_application()
    {
        await using var app = await BankingApi.StartAsync(fixture, enable: false);
        var envelope = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Private);
        await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{BankingApi.Root}/envelopes/{envelope.Value}/transactions"),
            HttpStatusCode.Conflict, BankingCodes.Disabled);

        await BankingApi.SettingsAsync(app, new { bankingEnabled = true });
        Assert.Empty(Labels(await ReadAsync(app, app.AdultA, envelope)));
    }

    private static async Task<(NidApp App, SettableClock Clock, Guid Account)> RetentionAsync(PostgresFixture fixture, object settings, bool withoutApplication = false)
    {
        var (app, clock) = await StartAsync(fixture);
        var (_, account, _) = await SetUpAsync(app);
        await BankingApi.SettingsAsync(app, settings);
        // The lines are all older than any the bank returns; the nightly run reads the (empty) bank, then purges.
        await AddAsync(app, account, Line("old", D(2025, 10, 1), -1m, "OLD"), Line("edge", D(2025, 10, 30), -2m, "EDGE"),
            Line("linked", D(2025, 1, 1), -3m, "LINKED"), Line("new", D(2026, 10, 1), -4m, "NEW"));
        await using (var db = Db(app))
            await db.Transactions.Where(t => t.ProviderRef == "linked").ExecuteUpdateAsync(s => s.SetProperty(t => t.LinkState, "linked"));
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        // Statement files alone need no Enable Banking application: the night has no bank to read, and still purges.
        if (withoutApplication)
            await BankingApi.SettingsAsync(app, new { enableBankingAppId = "" });
        var night = await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new SyncAllCommand());
        Assert.Equal(!withoutApplication, night.IsSuccess);
        return (app, clock, account);
    }

    [Fact]
    public async Task A_night_deletes_the_lines_older_than_the_retention_but_a_linked_one()
    {
        var (app, _, _) = await RetentionAsync(fixture, new { bankRetentionMonths = 12 });
        await using var _app = app;

        await using var db = Db(app);
        Assert.Equal(["EDGE", "LINKED", "NEW"], (await db.Transactions.Select(t => t.Label).ToListAsync(cancellationToken: TestContext.Current.CancellationToken)).Order());
    }

    [Fact]
    public async Task The_retention_runs_without_an_enable_banking_application()
    {
        var (app, _, _) = await RetentionAsync(fixture, new { bankRetentionMonths = 12 }, withoutApplication: true);
        await using var _app = app;

        await using var db = Db(app);
        Assert.Equal(3, await db.Transactions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Without_a_retention_a_night_deletes_nothing()
    {
        var (app, _, _) = await RetentionAsync(fixture, new { });
        await using var _app = app;

        await using var db = Db(app);
        Assert.Equal(4, await db.Transactions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_retention_is_set_cleared_and_bounded_through_the_admin_route()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        async Task<JsonElement> PatchAsync(string body)
            => await BankingApi.JsonAsync(await app.RawContentAsync(app.AdultA, HttpMethod.Patch, "/api/v1/admin/household",
                new StringContent(body, System.Text.Encoding.UTF8, "application/json")));

        Assert.Equal(12, (await PatchAsync("{\"bankRetentionMonths\":12}")).GetProperty("bankRetentionMonths").GetInt32());
        // A patch that leaves it out leaves it as it is; the flag clears it.
        Assert.Equal(12, (await PatchAsync("{\"bankingEnabled\":true}")).GetProperty("bankRetentionMonths").GetInt32());
        var refused = await app.RawContentAsync(app.AdultA, HttpMethod.Patch, "/api/v1/admin/household",
            new StringContent("{\"bankRetentionMonths\":601}", System.Text.Encoding.UTF8, "application/json"));
        await BankingApi.AssertRefusedAsync(refused, HttpStatusCode.UnprocessableEntity, PersonCodes.InvalidBankRetention);
        Assert.Equal("bankRetentionMonths", (await BankingApi.JsonAsync(refused)).GetProperty("errors")[0].GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.Null, (await PatchAsync("{\"clearBankRetention\":true}")).GetProperty("bankRetentionMonths").ValueKind);
    }
}
