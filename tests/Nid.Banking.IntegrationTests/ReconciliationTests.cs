using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Application.Reconciliation;
using Nid.Banking.Application.Sync;
using Nid.Banking.Contracts;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.TestSupport.ProposalKindHelpers;

namespace Nid.Banking.IntegrationTests;

/// <summary>5C spec §6.3: the pass over the lines not reconciled yet, after every sync and statement import.</summary>
public class ReconciliationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Iban = "FR7630006000011234567890189";
    private static readonly LocalDate Today = new(2026, 10, 29);

    private sealed class FixedClock : IClock
    {
        public Instant GetCurrentInstant() => Today.At(new LocalTime(10, 0)).InUtc().ToInstant();
    }

    private static LocalDate D(int month, int day) => new(2026, month, day);

    private static BankingDbContext Db(NidApp app) => new(new DbContextOptionsBuilder<BankingDbContext>().Configure(app.ConnectionString).Options);

    private static Task<NidApp> StartAsync(PostgresFixture fixture)
        => BankingApi.StartAsync(fixture, overrides: s => s.AddSingleton<IClock>(new FixedClock()));

    private static async Task<Guid> AccountAsync(NidApp app, PersonId person, string name)
        => (await BankingApi.AccountsAsync(app, person)).EnumerateArray().Single(a => a.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

    /// <summary>A's « Courant A ».</summary>
    private static async Task<Guid> CourantAsync(NidApp app, params SessionAccount[] more)
    {
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", [BankingApi.Account("uid-1", "hash-1", null, "Courant A"), .. more]);
        return await AccountAsync(app, app.AdultA, "Courant A");
    }

    /// <summary>The joint account, seen by B's connection first (B is its owner), then by A's.</summary>
    private static async Task<Guid> JointAsync(NidApp app)
    {
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-b", "hash-b", Iban, "Joint"));
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-a", "hash-a", Iban, "Joint"));
        return await AccountAsync(app, app.AdultA, "Joint");
    }

    private static async Task<Guid> AddAsync(NidApp app, Guid account, string reference, LocalDate on, decimal amount, string label = "PRLV SEPA NETFLIX 123")
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        await store.AddAsync(account, [new NewBankTransaction(reference, on, null, amount, Currency.Eur, label, null, null, "api")], default);
        return (await store.ListAsync([account], null, null, 0, 1000, default)).Single(t => t.ProviderRef == reference).Id;
    }

    /// <summary>
    /// A monthly subscription of <paramref name="price"/> (12.99) charged on the day of <paramref name="startsOn"/> (the 5th); private, so
    /// that its readers read « Courant A » (§0 n°1).
    /// </summary>
    private static async Task<ItemId> MonthlyAsync(
        NidApp app, string name = "Netflix", Visibility visibility = Visibility.Private, string startsOn = "2026-01-05", PersonId? owner = null,
        string price = "12.99")
    {
        var by = owner ?? app.AdultA;
        var id = await app.NewSubscriptionAsync(name, by, visibility);
        (await app.RawAsync(by, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn,
            price = new { amount = price, currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        return id;
    }

    private static async Task<BankRule> RuleAsync(NidApp app, Guid account, string stem, string module, ItemId target, PersonId by)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return await scope.ServiceProvider.GetRequiredService<IRuleStore>().UpsertAsync(account, stem, module, target, by, default);
    }

    /// <summary>The pass as the nightly job runs it: the system, no HTTP request.</summary>
    private static async Task<ReconciliationCounts> PassAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return await scope.ServiceProvider.GetRequiredService<BankReconciliation>().RunAsync(null, Today, default);
    }

    private static async Task<BankLink?> LinkOfAsync(NidApp app, Guid line)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return (await scope.ServiceProvider.GetRequiredService<ILinkStore>().ForTransactionsAsync([line], default)).GetValueOrDefault(line);
    }

    private static async Task<BankTransaction> LineAsync(NidApp app, Guid line)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return (await scope.ServiceProvider.GetRequiredService<ITransactionStore>().FindAsync(line, default))!;
    }

    private static async Task<JsonElement[]> PendingAsync(NidApp app, PersonId person, string kind = "banking.link_transaction")
        => [.. (await JsonOf(await app.RawAsync(person, HttpMethod.Get, $"{Proposals}?status=pending"))).GetProperty("items").EnumerateArray()
            .Where(p => p.GetProperty("kind").GetString() == kind)];

    private static async Task<int> UnreconciledAsync(NidApp app)
    {
        await using var db = Db(app);
        return await db.Transactions.CountAsync(t => t.ReconciledAt == null);
    }

    [Fact]
    public async Task A_transfer_between_two_accounts_is_internal()
    {
        await using var app = await StartAsync(fixture);
        var courant = await CourantAsync(app, BankingApi.Account("uid-2", "hash-2", null, "Livret A", "SVGS"));
        var livret = await AccountAsync(app, app.AdultA, "Livret A");
        var debit = await AddAsync(app, courant, "a", D(10, 5), -500m, "VIR VERS LIVRET");
        var credit = await AddAsync(app, livret, "b", D(10, 6), 500m, "VIR DE COURANT");

        var counts = await PassAsync(app);

        Assert.Equal(2, counts.Internal);
        var (outgoing, incoming) = ((await LinkOfAsync(app, debit))!, (await LinkOfAsync(app, credit))!);
        Assert.Equal((LinkOutcome.InternalTransfer, livret, "job"), (outgoing.Outcome, outgoing.CounterpartAccount, outgoing.DecidedBy));
        Assert.Equal((LinkOutcome.InternalTransfer, courant), (incoming.Outcome, incoming.CounterpartAccount));
        Assert.Equal((TransactionLinkState.Internal, TransactionLinkState.Internal), ((await LineAsync(app, debit)).LinkState, (await LineAsync(app, credit)).LinkState));
        Assert.Equal(0, await UnreconciledAsync(app));
    }

    [Fact]
    public async Task A_card_settlement_is_internal()
    {
        await using var app = await StartAsync(fixture);
        var courant = await CourantAsync(app, BankingApi.Account("uid-2", "hash-2", null, "Carte A", "CARD"));
        var card = await AccountAsync(app, app.AdultA, "Carte A");
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{card}", new { cardOfAccountId = courant })).StatusCode);
        var bread = await AddAsync(app, card, "c1", D(9, 10), -30m, "CB BOULANGERIE");
        await AddAsync(app, card, "c2", D(9, 20), -20.5m, "CB LIBRAIRIE");
        var settlement = await AddAsync(app, courant, "s", D(10, 1), -50.5m, "CB DIFFERE");

        await PassAsync(app);

        var link = (await LinkOfAsync(app, settlement))!;
        Assert.Equal((LinkOutcome.CardSettlement, card), (link.Outcome, link.CounterpartAccount));
        Assert.Equal(TransactionLinkState.Internal, (await LineAsync(app, settlement)).LinkState);
        Assert.Equal(TransactionLinkState.Unlinked, (await LineAsync(app, bread)).LinkState);
    }

    [Fact]
    public async Task A_rule_links_the_next_month_without_proposal()
    {
        await using var app = await StartAsync(fixture);
        var courant = await CourantAsync(app);
        var netflix = await MonthlyAsync(app);
        var rule = await RuleAsync(app, courant, "NETFLIX", "subscriptions", netflix, app.AdultA);
        var line = await AddAsync(app, courant, "a", D(10, 5), -12.99m);

        var counts = await PassAsync(app);

        var link = (await LinkOfAsync(app, line))!;
        Assert.Equal((LinkOutcome.AsExpected, "2026-10-05", $"rule:{rule.Id:D}"), (link.Outcome, link.TargetKey, link.DecidedBy));
        Assert.Equal((1, 0), (counts.Linked, counts.Proposed));
        Assert.Empty(await PendingAsync(app, app.AdultA));
    }

    [Fact]
    public async Task A_single_candidate_is_proposed_once()
    {
        await using var app = await StartAsync(fixture);
        var courant = await CourantAsync(app);
        var netflix = await MonthlyAsync(app);
        var line = await AddAsync(app, courant, "a", D(10, 5), -12.99m);

        Assert.Equal(1, (await PassAsync(app)).Proposed);
        Assert.Equal(0, (await PassAsync(app)).Proposed);

        var proposal = Assert.Single(await PendingAsync(app, app.AdultA));
        var payload = proposal.GetProperty("payload");
        Assert.Equal((line, "subscriptions", netflix.Value, "2026-10-05"), (payload.GetProperty("transactionId").GetGuid(),
            payload.GetProperty("module").GetString(), payload.GetProperty("itemId").GetGuid(), payload.GetProperty("key").GetString()));
        Assert.Equal("Banque", proposal.GetProperty("sourceLabel").GetString());
        Assert.Null(await LinkOfAsync(app, line));
    }

    [Fact]
    public async Task A_long_history_asks_once_per_item()
    {
        await using var app = await StartAsync(fixture);
        var courant = await CourantAsync(app);
        await MonthlyAsync(app, startsOn: "2024-11-05");
        var lines = new List<Guid>();
        for (var month = new LocalDate(2024, 11, 5); month <= D(10, 5); month = month.PlusMonths(1))
            lines.Add(await AddAsync(app, courant, $"n-{month:yyyy-MM}", month, -12.99m));
        Assert.Equal(24, lines.Count);

        await PassAsync(app);

        var proposal = Assert.Single(await PendingAsync(app, app.AdultA));
        Assert.Equal(lines[^1], proposal.GetProperty("payload").GetProperty("transactionId").GetGuid());
        Assert.Equal(0, await UnreconciledAsync(app));
    }

    [Fact]
    public async Task Only_an_item_s_latest_recent_line_is_asked()
    {
        await using var app = await StartAsync(fixture);
        var courant = await CourantAsync(app);
        await MonthlyAsync(app, startsOn: "2026-01-20");
        await MonthlyAsync(app, "Spotify", startsOn: "2026-01-01", price: "9.99");
        await AddAsync(app, courant, "n-09", D(9, 20), -12.99m); // 39 days before today: within the horizon
        var latest = await AddAsync(app, courant, "n-10", D(10, 20), -12.99m);
        await AddAsync(app, courant, "s-09", D(9, 1), -9.99m, "PRLV SEPA SPOTIFY 123"); // 58 days before today

        await PassAsync(app);

        Assert.Equal(latest, Assert.Single(await PendingAsync(app, app.AdultA)).GetProperty("payload").GetProperty("transactionId").GetGuid());
        Assert.Equal(0, await UnreconciledAsync(app));
    }

    [Fact]
    public async Task A_failing_step_leaves_its_line_for_the_next_pass_and_the_others_go_on()
    {
        var failing = new FailingCards();
        await using var app = await BankingApi.StartAsync(fixture, overrides: s =>
        {
            s.AddSingleton<IClock>(new FixedClock());
            s.AddSingleton(failing);
            s.AddScoped<ITransactionStore>(sp => new FailingCardLines(ActivatorUtilities.CreateInstance<TransactionStore>(sp), failing));
        });
        var courant = await CourantAsync(app,
            BankingApi.Account("uid-2", "hash-2", null, "Carte A", "CARD"), BankingApi.Account("uid-3", "hash-3", null, "Livret A", "SVGS"));
        var (card, livret) = (await AccountAsync(app, app.AdultA, "Carte A"), await AccountAsync(app, app.AdultA, "Livret A"));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{card}", new { cardOfAccountId = courant })).StatusCode);
        await MonthlyAsync(app);
        await AddAsync(app, card, "c1", D(9, 20), -30m, "CB BOULANGERIE");
        var settlement = await AddAsync(app, courant, "s", D(10, 1), -30m, "CB DIFFERE");
        var debit = await AddAsync(app, courant, "t", D(10, 5), -500m, "VIR VERS LIVRET");
        await AddAsync(app, livret, "u", D(10, 6), 500m, "VIR DE COURANT");
        await AddAsync(app, livret, "n", D(10, 5), -12.99m); // no card on a savings account: step 2 never reads it
        failing.Accounts.Add(card);

        var counts = await PassAsync(app);

        Assert.Equal((2, 1), (counts.Internal, counts.Proposed));
        Assert.Equal(TransactionLinkState.Internal, (await LineAsync(app, debit)).LinkState);
        Assert.Equal(TransactionLinkState.Unlinked, (await LineAsync(app, settlement)).LinkState);
        await using var db = Db(app);
        Assert.Equal([settlement], await db.Transactions.Where(t => t.ReconciledAt == null).Select(t => t.Id).ToListAsync());
    }

    [Fact]
    public async Task A_failed_settlement_load_skips_step_2_only()
    {
        await using var app = await BankingApi.StartAsync(fixture, overrides: s =>
        {
            s.AddSingleton<IClock>(new FixedClock());
            s.AddScoped<ILinkStore>(sp => new FailingSettlements(ActivatorUtilities.CreateInstance<LinkStore>(sp)));
        });
        var courant = await CourantAsync(app,
            BankingApi.Account("uid-2", "hash-2", null, "Carte A", "CARD"), BankingApi.Account("uid-3", "hash-3", null, "Livret A", "SVGS"));
        var (card, livret) = (await AccountAsync(app, app.AdultA, "Carte A"), await AccountAsync(app, app.AdultA, "Livret A"));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{card}", new { cardOfAccountId = courant })).StatusCode);
        await MonthlyAsync(app);
        await AddAsync(app, card, "c1", D(9, 20), -30m, "CB BOULANGERIE");
        var settlement = await AddAsync(app, courant, "s", D(10, 1), -30m, "CB DIFFERE");
        var netflix = await AddAsync(app, livret, "n", D(10, 5), -12.99m);

        var counts = await PassAsync(app);

        Assert.Equal(1, counts.Proposed);
        Assert.Equal(netflix, Assert.Single(await PendingAsync(app, app.AdultA)).GetProperty("payload").GetProperty("transactionId").GetGuid());
        await using var db = Db(app);
        Assert.Equal([settlement], await db.Transactions.Where(t => t.ReconciledAt == null).Select(t => t.Id).ToListAsync());
    }

    [Fact]
    public async Task A_rule_its_creator_cannot_write_links_nothing()
    {
        await using var app = await StartAsync(fixture);
        var joint = await JointAsync(app);
        // A's own subscription, private: B's rule to it is stale.
        var secret = await MonthlyAsync(app, visibility: Visibility.Private);
        await RuleAsync(app, joint, "NETFLIX", "subscriptions", secret, app.AdultB);
        var line = await AddAsync(app, joint, "a", D(10, 5), -12.99m);

        var counts = await PassAsync(app);

        Assert.Equal(0, counts.Linked);
        Assert.Null(await LinkOfAsync(app, line));
        // A stale rule does not stand in the way: A, who writes the subscription, is asked.
        Assert.Equal(line, Assert.Single(await PendingAsync(app, app.AdultA)).GetProperty("payload").GetProperty("transactionId").GetGuid());
        await using var scope = app.Services.CreateAsyncScope();
        Assert.Null(Assert.Single(await scope.ServiceProvider.GetRequiredService<IRuleStore>().ByAsync(app.AdultB, default)).LastUsedAt);
    }

    [Fact]
    public async Task Two_rules_to_different_items_link_nothing()
    {
        await using var app = await StartAsync(fixture);
        var joint = await JointAsync(app);
        var netflix = await MonthlyAsync(app);
        var other = await MonthlyAsync(app, "Netflix B", owner: app.AdultB);
        await RuleAsync(app, joint, "NETFLIX", "subscriptions", netflix, app.AdultA);
        await RuleAsync(app, joint, "NETFLIX", "subscriptions", other, app.AdultB);
        var line = await AddAsync(app, joint, "a", D(10, 5), -12.99m);

        var counts = await PassAsync(app);

        Assert.Equal((0, 0), (counts.Linked, counts.Proposed));
        Assert.Null(await LinkOfAsync(app, line));
        Assert.Equal(0, await UnreconciledAsync(app));
    }

    [Fact]
    public async Task The_depositor_writes_the_target()
    {
        await using var app = await StartAsync(fixture);
        var joint = await JointAsync(app);
        var secret = await MonthlyAsync(app, visibility: Visibility.Private);
        var line = await AddAsync(app, joint, "a", D(10, 5), -12.99m);

        await PassAsync(app);

        var proposal = Assert.Single(await PendingAsync(app, app.AdultA));
        Assert.Equal((app.AdultA.Value, secret.Value), (proposal.GetProperty("createdByPersonId").GetGuid(), proposal.GetProperty("targetItemId").GetGuid()));
        Assert.Equal(line, proposal.GetProperty("payload").GetProperty("transactionId").GetGuid());
        Assert.Empty(await PendingAsync(app, app.AdultB));
    }

    [Fact]
    public async Task A_rule_linked_rent_in_the_night_proposes_its_cash_in()
    {
        await using var app = await StartAsync(fixture);
        var courant = await CourantAsync(app);
        var property = await PropertyApi.NewPropertyAsync(app, PropertyApi.Home(app, visibility: "private"));
        await LoanApi.CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{PropertyApi.Properties}/{property}/tenancies",
            new { label = "Bail", startsOn = "2025-01-01", rentMonthly = "750", chargesMonthly = "50" }));
        await RuleAsync(app, courant, "DUPONT LOYER", "realestate", new ItemId(property), app.AdultA);
        app.Banks.Balances[("session-code-a", "uid-1")] = [new ProviderBalance("CLBD", 1000m, "EUR", Today)];
        app.Banks.Transactions[("session-code-a", "uid-1")] = [new ProviderTransaction("r1", D(10, 5), null, 800m, "EUR", "VIR SEPA DUPONT LOYER", null, null)];

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            Assert.True((await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new SyncAllCommand())).IsSuccess);
        }

        await using var db = Db(app);
        var line = (await db.Transactions.SingleAsync()).Id;
        Assert.Equal((LinkOutcome.Income, "rent:2026-10"), ((await LinkOfAsync(app, line))!.Outcome, (await LinkOfAsync(app, line))!.TargetKey));
        var cashIn = Assert.Single(await PendingAsync(app, app.AdultA, "realestate.add_property_flow"));
        Assert.Equal(app.AdultA.Value, cashIn.GetProperty("createdByPersonId").GetGuid());
    }

    [Fact]
    public async Task A_manual_sync_reconciles_its_accounts()
    {
        await using var app = await StartAsync(fixture);
        await CourantAsync(app);
        await MonthlyAsync(app);
        app.Banks.Balances[("session-code-a", "uid-1")] = [new ProviderBalance("CLBD", 1000m, "EUR", Today)];
        app.Banks.Transactions[("session-code-a", "uid-1")] = [new ProviderTransaction("r1", D(10, 5), null, -12.99m, "EUR", "PRLV SEPA NETFLIX 123", null, null)];

        Assert.Equal(HttpStatusCode.Accepted, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/sync", new { })).StatusCode);

        Assert.Single(await PendingAsync(app, app.AdultA));
        Assert.Equal(0, await UnreconciledAsync(app));
    }

    [Fact]
    public async Task A_statement_import_reconciles_its_account()
    {
        await using var app = await StartAsync(fixture);
        await MonthlyAsync(app);
        var ofx = Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <?OFX OFXHEADER="200" VERSION="211" SECURITY="NONE" OLDFILEUID="NONE" NEWFILEUID="NONE"?>
            <OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS>
            <CURDEF>EUR</CURDEF>
            <BANKACCTFROM><ACCTID>{Iban}</ACCTID><ACCTTYPE>CHECKING</ACCTTYPE></BANKACCTFROM>
            <BANKTRANLIST><DTSTART>20261005</DTSTART><DTEND>20261005</DTEND>
            <STMTTRN><TRNTYPE>OTHER</TRNTYPE><DTPOSTED>20261005</DTPOSTED><TRNAMT>-12.99</TRNAMT><FITID>F1</FITID><NAME>PRLV SEPA NETFLIX 123</NAME></STMTTRN>
            </BANKTRANLIST>
            </STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
            """);

        var imported = await app.RawContentAsync(app.AdultA, HttpMethod.Post, BankingApi.Root + "/statements",
            DocumentApi.Multipart(ofx, "fileName=releve.ofx&confirm=true&keepProbablyKnown=false"));

        Assert.True(imported.IsSuccessStatusCode, await imported.Content.ReadAsStringAsync());
        Assert.Single(await PendingAsync(app, app.AdultA));
        Assert.Equal(0, await UnreconciledAsync(app));
    }

    [Fact]
    public async Task A_nid_data_import_proposes_nothing()
    {
        await using var app = await StartAsync(fixture);
        await MonthlyAsync(app);
        var file = new Dictionary<string, string>
        {
            ["persons"] = """[{"ref":"adult-1","kind":"adult","display_name":"A"}]""",
            ["bank_accounts"] = """[{"ref":"courant","name":"Courant","kind":"checking","currency":"EUR","owner_ref":"adult-1"}]""",
            ["bank_transactions"] = """[{"account_ref":"courant","provider_ref":"r-1","fingerprint":"x","booked_on":"2026-10-05","amount":"-12.99","currency":"EUR","label":"PRLV SEPA NETFLIX 123","fetched_via":"api"}]""",
        };

        var (report, _) = await BankImportTests.ImportAsync(app, app.AdultA, file);
        var counts = await PassAsync(app);

        Assert.Equal("committed", report.GetProperty("state").GetString());
        Assert.Equal(0, counts.Proposed);
        Assert.Empty(await PendingAsync(app, app.AdultA));
        Assert.Equal(0, await UnreconciledAsync(app));
    }

    /// <summary>A's « Courant A » on A's shared envelope: B reads it, only A writes it.</summary>
    private static async Task<Guid> ReadByBAsync(NidApp app)
    {
        var courant = await CourantAsync(app);
        var envelope = await app.NewEnvelopeAsync("Commun A", Visibility.Shared, (app.AdultA, 10_000));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{courant}", new { envelopeId = envelope.Value })).StatusCode);
        return courant;
    }

    [Fact]
    public async Task A_shared_item_paid_from_a_private_account_is_not_proposed()
    {
        await using var app = await StartAsync(fixture);
        var courant = await CourantAsync(app);
        await MonthlyAsync(app, visibility: Visibility.Shared); // B reads it, not « Courant A »
        var line = await AddAsync(app, courant, "a", D(10, 5), -12.99m);

        var counts = await PassAsync(app);

        Assert.Equal(0, counts.Proposed);
        Assert.Empty(await PendingAsync(app, app.AdultA));
        Assert.Empty(await PendingAsync(app, app.AdultB));
        Assert.Null(await LinkOfAsync(app, line));
    }

    [Fact]
    public async Task A_rule_on_a_private_account_links_a_shared_item_without_telling_B()
    {
        await using var app = await StartAsync(fixture);
        var courant = await CourantAsync(app);
        var netflix = await MonthlyAsync(app, visibility: Visibility.Shared);
        await RuleAsync(app, courant, "NETFLIX", "subscriptions", netflix, app.AdultA);
        var line = await AddAsync(app, courant, "a", D(10, 5), -13.99m);

        await PassAsync(app);

        Assert.Equal(LinkOutcome.PriceDiffers, (await LinkOfAsync(app, line))!.Outcome);
        Assert.Empty(await PendingAsync(app, app.AdultA, "subscriptions.change_price"));
        Assert.Empty(await PendingAsync(app, app.AdultB, "subscriptions.change_price"));
        // The rule and the link are A's account's: the subscription's history, which B reads, shows neither.
        var history = await JsonOf(await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/platform/audit/items/{netflix.Value}"));
        Assert.DoesNotContain(history.GetProperty("entries").EnumerateArray(), e => e.GetProperty("entity").GetString()!.StartsWith("banking.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_adults_private_accounts_never_pair()
    {
        await using var app = await StartAsync(fixture);
        var courant = await CourantAsync(app);
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-b", "hash-b", null, "Courant B"));
        var ofB = await AccountAsync(app, app.AdultB, "Courant B");
        var debit = await AddAsync(app, courant, "a", D(10, 5), -13.49m);
        var credit = await AddAsync(app, ofB, "b", D(10, 6), 13.49m, "VIR VENTE VELO");

        var counts = await PassAsync(app);

        Assert.Equal(0, counts.Internal);
        Assert.Equal((TransactionLinkState.Unlinked, TransactionLinkState.Unlinked), ((await LineAsync(app, debit)).LinkState, (await LineAsync(app, credit)).LinkState));
    }

    [Fact]
    public async Task A_card_line_never_pairs()
    {
        await using var app = await StartAsync(fixture);
        var courant = await CourantAsync(app, BankingApi.Account("uid-2", "hash-2", null, "Carte A", "CARD"), BankingApi.Account("uid-3", "hash-3", null, "Livret A", "SVGS"));
        var card = await AccountAsync(app, app.AdultA, "Carte A");
        var livret = await AccountAsync(app, app.AdultA, "Livret A");
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{card}", new { cardOfAccountId = courant })).StatusCode);
        var debit = await AddAsync(app, livret, "l", D(10, 5), -50m, "VIR VERS COURANT");
        var refund = await AddAsync(app, card, "c", D(10, 6), 50m, "AVOIR BOUTIQUE");

        var counts = await PassAsync(app);

        Assert.Equal(0, counts.Internal);
        Assert.Equal((TransactionLinkState.Unlinked, TransactionLinkState.Unlinked), ((await LineAsync(app, debit)).LinkState, (await LineAsync(app, refund)).LinkState));
    }

    [Fact]
    public async Task A_rule_whose_creator_only_reads_the_account_links_nothing()
    {
        await using var app = await StartAsync(fixture);
        var courant = await ReadByBAsync(app);
        var ofB = await MonthlyAsync(app, "Netflix B", Visibility.Private, owner: app.AdultB);
        await RuleAsync(app, courant, "NETFLIX", "subscriptions", ofB, app.AdultB);
        var line = await AddAsync(app, courant, "a", D(10, 5), -12.99m);

        var counts = await PassAsync(app);

        Assert.Equal(0, counts.Linked);
        Assert.Null(await LinkOfAsync(app, line));
    }

    [Fact]
    public async Task A_reader_who_does_not_write_the_account_is_not_asked()
    {
        await using var app = await StartAsync(fixture);
        var courant = await ReadByBAsync(app);
        await MonthlyAsync(app, "Netflix B", Visibility.Private, owner: app.AdultB);
        await AddAsync(app, courant, "a", D(10, 5), -12.99m);

        var counts = await PassAsync(app);

        Assert.Equal(0, counts.Proposed);
        Assert.Empty(await PendingAsync(app, app.AdultB));
    }

    [Fact]
    public async Task A_manual_sync_proposes_the_cash_in_of_B_rule_as_B()
    {
        await using var app = await StartAsync(fixture);
        var joint = await JointAsync(app);
        // B's rental, shared: A reads it but does not write it.
        var property = await PropertyApi.NewPropertyAsync(app, PropertyApi.Home(app, "Studio", "shared", app.AdultB), app.AdultB);
        await LoanApi.CreatedAsync(await app.RawAsync(app.AdultB, HttpMethod.Post, $"{PropertyApi.Properties}/{property}/tenancies",
            new { label = "Bail", startsOn = "2025-01-01", rentMonthly = "750", chargesMonthly = "50" }));
        await RuleAsync(app, joint, "DUPONT LOYER", "realestate", new ItemId(property), app.AdultB);
        app.Banks.Balances[("session-code-a", "uid-a")] = [new ProviderBalance("CLBD", 1000m, "EUR", Today)];
        app.Banks.Transactions[("session-code-a", "uid-a")] = [new ProviderTransaction("r1", D(10, 5), null, 800m, "EUR", "VIR SEPA DUPONT LOYER", null, null)];

        Assert.Equal(HttpStatusCode.Accepted, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/sync", new { })).StatusCode);

        var cashIn = Assert.Single(await PendingAsync(app, app.AdultB, "realestate.add_property_flow"));
        Assert.Equal((app.AdultB.Value, property), (cashIn.GetProperty("createdByPersonId").GetGuid(), cashIn.GetProperty("targetItemId").GetGuid()));
    }

    /// <summary>The link store, but the card settlements cannot be read.</summary>
    private sealed class FailingSettlements(ILinkStore inner) : ILinkStore
    {
        public Task<IReadOnlyList<(BankLink Link, BankTransaction Line)>> OutcomeSinceAsync(LinkOutcome outcome, LocalDate since, CancellationToken ct)
            => outcome == LinkOutcome.CardSettlement
                ? throw new InvalidOperationException("The settlements cannot be read.")
                : inner.OutcomeSinceAsync(outcome, since, ct);

        public Task<IReadOnlyDictionary<Guid, BankLink>> ForTransactionsAsync(IReadOnlyCollection<Guid> transactions, CancellationToken ct)
            => inner.ForTransactionsAsync(transactions, ct);

        public Task<IReadOnlyList<(BankLink Link, BankTransaction Line)>> ForItemsAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct)
            => inner.ForItemsAsync(items, ct);

        public Task SetAsync(BankLink link, TransactionLinkState state, CancellationToken ct) => inner.SetAsync(link, state, ct);

        public Task ClearAsync(Guid transaction, TransactionLinkState state, CancellationToken ct) => inner.ClearAsync(transaction, state, ct);

        public Task<bool> FlowTakenAsync(Guid flowId, CancellationToken ct) => inner.FlowTakenAsync(flowId, ct);
    }

    /// <summary>The card accounts whose lines cannot be read.</summary>
    private sealed class FailingCards
    {
        public HashSet<Guid> Accounts { get; } = [];
    }

    /// <summary>The store, but reading the lines of a <see cref="FailingCards"/> account throws.</summary>
    private sealed class FailingCardLines(ITransactionStore inner, FailingCards failing) : ITransactionStore
    {
        public Task<IReadOnlyList<BankTransaction>> ListAsync(
            IReadOnlyList<Guid> accounts, LocalDate? from, LocalDate? to, int skip, int take, CancellationToken ct, string? state = null,
            IReadOnlyList<Guid>? readable = null)
            => accounts.Any(failing.Accounts.Contains)
                ? throw new InvalidOperationException("The card's lines cannot be read.")
                : inner.ListAsync(accounts, from, to, skip, take, ct, state, readable);

        public Task<TransactionWrite> AddAsync(Guid account, IReadOnlyList<NewBankTransaction> lines, CancellationToken ct, bool reconciled = false)
            => inner.AddAsync(account, lines, ct, reconciled);

        public Task<LocalDate?> LatestBookedOnAsync(Guid account, string? fetchedVia, CancellationToken ct) => inner.LatestBookedOnAsync(account, fetchedVia, ct);

        public Task<bool> HasFetchedViaAsync(Guid account, string fetchedVia, CancellationToken ct) => inner.HasFetchedViaAsync(account, fetchedVia, ct);

        public Task<IReadOnlyList<BankTransaction>> NearAsync(Guid account, IReadOnlyList<(LocalDate BookedOn, decimal Amount)> lines, CancellationToken ct)
            => inner.NearAsync(account, lines, ct);

        public Task<IReadOnlyList<BankTransaction>> UnreconciledAsync(IReadOnlyCollection<Guid>? accounts, CancellationToken ct) => inner.UnreconciledAsync(accounts, ct);

        public Task<IReadOnlyList<BankTransaction>> UnlinkedBetweenAsync(LocalDate from, LocalDate to, CancellationToken ct) => inner.UnlinkedBetweenAsync(from, to, ct);

        public Task<BankTransaction?> FindAsync(Guid id, CancellationToken ct) => inner.FindAsync(id, ct);

        public Task MarkReconciledAsync(IReadOnlyCollection<Guid> ids, Instant at, CancellationToken ct) => inner.MarkReconciledAsync(ids, at, ct);

        public Task<int> PurgeBookedBeforeAsync(LocalDate before, CancellationToken ct) => inner.PurgeBookedBeforeAsync(before, ct);
    }
}
