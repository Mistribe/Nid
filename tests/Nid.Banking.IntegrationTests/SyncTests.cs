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
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.IntegrationTests;

/// <summary>Bank sync spec §3.2, §3.3 and §4: the nightly and manual sync, balances read once per account, balances becoming valuations.</summary>
public class SyncTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string JointIban = "FR76 3000 6000 0112 3456 7890 189";

    private sealed class SettableClock(Instant now) : IClock
    {
        public Instant Now { get; set; } = now;

        public Instant GetCurrentInstant() => Now;
    }

    private static LocalDate D(int month, int day) => new(2026, month, day);

    private static Instant At(LocalDate day) => day.At(new LocalTime(10, 0)).InUtc().ToInstant();

    private static BankingDbContext Db(NidApp app) => new(new DbContextOptionsBuilder<BankingDbContext>().Configure(app.ConnectionString).Options);

    private static async Task<(NidApp App, SettableClock Clock)> StartAsync(PostgresFixture fixture)
    {
        var clock = new SettableClock(At(D(8, 12)));
        return (await BankingApi.StartAsync(fixture, overrides: s => s.AddSingleton<IClock>(clock)), clock);
    }

    private static void Serve(NidApp app, string session, string uid, LocalDate on, decimal amount, string type = "CLBD", string currency = "EUR")
        => app.Banks.Balances[(session, uid)] = [new ProviderBalance(type, amount, currency, on)];

    /// <summary>What the <c>bank-sync-nightly</c> job sends, as the system.</summary>
    private static async Task<Result<BankSyncRunView>> NightlyAsync(NidApp app, SettableClock clock, LocalDate on)
    {
        clock.Now = At(on);
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new SyncAllCommand());
    }

    private static async Task<BankSyncRunView> NightAsync(NidApp app, SettableClock clock, LocalDate on)
    {
        var result = await NightlyAsync(app, clock, on);
        Assert.True(result.TryGetValue(out var run), result.TryGetFailure(out var failure) ? failure!.Code : "");
        return run!;
    }

    private static async Task<Guid> AccountAsync(NidApp app, PersonId person, string name)
        => (await BankingApi.AccountsAsync(app, person)).EnumerateArray().Single(a => a.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

    private static async Task AssociateAsync(NidApp app, PersonId person, Guid account, ItemId envelope, bool auto = false)
        => Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(person, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{account}",
            new { envelopeId = envelope.Value, autoApplyBalance = auto })).StatusCode);

    private static async Task<List<JsonElement>> BankProposalsAsync(NidApp app, PersonId person)
        => JsonDocument.Parse(await app.GetRawAsync(person, ProposalKindHelpers.Proposals)).RootElement.GetProperty("items").EnumerateArray()
            .Where(p => p.GetProperty("sourceLabel").GetString() == "Banque").OrderBy(p => p.GetProperty("subjectKey").GetString()).ToList();

    /// <summary>A's savings account on an envelope of A's, then nights on 08-20, 08-31, 09-15 and 10-02 with a balance each day.</summary>
    private static async Task<(NidApp App, SettableClock Clock, Guid Account, ItemId Envelope)> MonthsAsync(
        PostgresFixture fixture, bool auto, Visibility visibility = Visibility.Shared)
    {
        var (app, clock) = await StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("Livret", app.AdultA, visibility);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Livret A", "SVGS"));
        var account = await AccountAsync(app, app.AdultA, "Livret A");
        await AssociateAsync(app, app.AdultA, account, envelope, auto);
        foreach (var (on, amount) in new[] { (D(8, 20), 900m), (D(8, 31), 1000m), (D(9, 15), 1100m), (D(10, 2), 1200m) })
        {
            Serve(app, "session-code-a", "uid-1", on, amount);
            await NightAsync(app, clock, on);
        }

        return (app, clock, account, envelope);
    }

    [Fact]
    public async Task Nightly_proposes_each_elapsed_month_end_for_the_connection_creator()
    {
        var (app, _, _, envelope) = await MonthsAsync(fixture, auto: false);
        await using var _app = app;

        var proposals = await BankProposalsAsync(app, app.AdultA);
        Assert.Equal(["2026-08-31", "2026-09-30"], proposals.Select(p => p.GetProperty("subjectKey").GetString()));
        var august = proposals[0];
        Assert.Equal(("investments.record_valuation", envelope.Value, app.AdultA.Value, "job"),
            (august.GetProperty("kind").GetString(), august.GetProperty("targetItemId").GetGuid(), august.GetProperty("createdByPersonId").GetGuid(),
                august.GetProperty("createdVia").GetString()));
        Assert.Equal(("2026-08-31", 1000m, "Solde bancaire du 31/08/2026"),
            (august.GetProperty("payload").GetProperty("asOf").GetString(), decimal.Parse(august.GetProperty("payload").GetProperty("amount").GetProperty("amount").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture), august.GetProperty("payload").GetProperty("note").GetString()));
        // September's end takes the month's last balance.
        Assert.Equal("Solde bancaire du 15/09/2026", proposals[1].GetProperty("payload").GetProperty("note").GetString());
        // The envelope is shared: B reads its proposals.
        Assert.Equal(2, (await BankProposalsAsync(app, app.AdultB)).Count);
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.False(await core.Valuations.AnyAsync(v => v.ItemId == envelope.Value, cancellationToken: TestContext.Current.CancellationToken));
        await using var db = Db(app);
        Assert.Equal(D(9, 30), (await db.Accounts.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).LastValuedOn);
    }

    [Fact]
    public async Task Auto_apply_writes_bank_valuations_without_proposals()
    {
        var (app, _, _, envelope) = await MonthsAsync(fixture, auto: true);
        await using var _app = app;

        Assert.Empty(await BankProposalsAsync(app, app.AdultA));
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var values = await core.Valuations.Where(v => v.ItemId == envelope.Value).OrderBy(v => v.AsOf).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([(D(8, 31), 1000m, "Solde bancaire du 31/08/2026"), (D(9, 30), 1100m, "Solde bancaire du 15/09/2026")],
            values.Select(v => (v.AsOf, v.Amount, v.Note)));
        Assert.All(values, v => Assert.Equal(("bank_api", "job", app.AdultA.Value), (v.Source, v.CreatedVia, v.CreatedByPersonId)));
        await using var db = Db(app);
        var run = await db.SyncRuns.OrderByDescending(r => r.StartedAt).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(("schedule", "succeeded", 1, 1, 1, 0, 1), (run.Trigger, run.Outcome, run.Connections, run.Accounts, run.Balances, run.Proposals, run.Written));
    }

    [Fact]
    public async Task Manual_valuation_is_never_replaced()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        var envelope = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Private);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Livret A", "SVGS"));
        var account = await AccountAsync(app, app.AdultA, "Livret A");
        await AssociateAsync(app, app.AdultA, account, envelope, auto: true);
        Serve(app, "session-code-a", "uid-1", D(8, 31), 1000m);
        await NightAsync(app, clock, D(8, 31));
        clock.Now = At(D(9, 1));
        Assert.Null((await app.InvokeAsync(app.AdultA, new Nid.Investments.Application.RecordValuationCommand(
            envelope, D(8, 31), Money.Create(777m, Currency.Eur).Ok(), "à la main"), TestContext.Current.CancellationToken)).Failure);

        var run = await NightAsync(app, clock, D(9, 2));

        Assert.Equal((0, 1), (run.Written, run.Skipped));
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var value = await core.Valuations.SingleAsync(v => v.ItemId == envelope.Value && v.RetiredAt == null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal((777m, "manual"), (value.Amount, value.Source));
        var read = Assert.Single((await BankingApi.AccountsAsync(app, app.AdultA, envelope)).EnumerateArray());
        Assert.Equal("2026-08-31", read.GetProperty("balanceConflictOn").GetString());
        // Done: the next night does not try again.
        Assert.Equal(0, (await NightAsync(app, clock, D(9, 3))).Skipped);
        // Once a later month-end is valued, the warning goes.
        Serve(app, "session-code-a", "uid-1", D(9, 30), 1100m);
        await NightAsync(app, clock, D(9, 30));
        Assert.Equal(1, (await NightAsync(app, clock, D(10, 1))).Written);
        read = Assert.Single((await BankingApi.AccountsAsync(app, app.AdultA, envelope)).EnumerateArray());
        Assert.Equal(JsonValueKind.Null, read.GetProperty("balanceConflictOn").ValueKind);
    }

    [Fact]
    public async Task A_second_click_the_same_day_leaves_an_approved_bank_value_alone()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        var envelope = await app.NewEnvelopeAsync("Courant", app.AdultA, Visibility.Private);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        await AssociateAsync(app, app.AdultA, await AccountAsync(app, app.AdultA, "Courant A"), envelope);
        Serve(app, "session-code-a", "uid-1", D(8, 20), 42m);
        clock.Now = At(D(8, 20));
        Assert.Equal(HttpStatusCode.Accepted, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/sync", new { })).StatusCode);
        // Approved, the bank's value is written as the person's.
        Assert.Equal(HttpStatusCode.OK, (await ProposalKindHelpers.ApproveAsync(app, Assert.Single(await BankProposalsAsync(app, app.AdultA)))).StatusCode);

        Assert.Equal(HttpStatusCode.Accepted, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/sync", new { })).StatusCode);

        var read = Assert.Single((await BankingApi.AccountsAsync(app, app.AdultA, envelope)).EnumerateArray());
        Assert.Equal(JsonValueKind.Null, read.GetProperty("balanceConflictOn").ValueKind);
        await using var db = Db(app);
        Assert.Equal(0, (await db.SyncRuns.OrderByDescending(r => r.StartedAt).FirstAsync(cancellationToken: TestContext.Current.CancellationToken)).Skipped);
    }

    [Fact]
    public async Task A_click_values_today_only_as_whoever_clicked()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-a", "hash-a", JointIban, "Joint"));
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-b", "hash-b", JointIban, "Joint"));
        var ofB = await app.NewEnvelopeAsync("Courant de B", app.AdultB, Visibility.Private);
        await AssociateAsync(app, app.AdultB, await AccountAsync(app, app.AdultB, "Joint"), ofB);
        Serve(app, "session-code-a", "uid-a", D(8, 20), 100m);
        Serve(app, "session-code-b", "uid-b", D(8, 20), 100m);
        clock.Now = At(D(8, 20));

        // A's click reads the joint account, but A does not write B's envelope: no value of today in B's name.
        Assert.Equal(HttpStatusCode.Accepted, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/sync", new { })).StatusCode);
        Assert.Empty(await BankProposalsAsync(app, app.AdultB));

        Assert.Equal(HttpStatusCode.Accepted, (await app.RawAsync(app.AdultB, HttpMethod.Post, $"{BankingApi.Root}/sync", new { })).StatusCode);
        var proposal = Assert.Single(await BankProposalsAsync(app, app.AdultB));
        Assert.Equal(("2026-08-20", app.AdultB.Value), (proposal.GetProperty("subjectKey").GetString(), proposal.GetProperty("createdByPersonId").GetGuid()));
    }

    [Fact]
    public async Task An_available_balance_is_noted_as_such()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        var envelope = await app.NewEnvelopeAsync("Courant", app.AdultA, Visibility.Private);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        await AssociateAsync(app, app.AdultA, await AccountAsync(app, app.AdultA, "Courant A"), envelope);
        Serve(app, "session-code-a", "uid-1", D(8, 31), 50m, "CLAV");
        await NightAsync(app, clock, D(8, 31));

        await NightAsync(app, clock, D(9, 1));

        Assert.Equal("Solde bancaire du 31/08/2026 (solde disponible)",
            Assert.Single(await BankProposalsAsync(app, app.AdultA)).GetProperty("payload").GetProperty("note").GetString());
    }

    [Fact]
    public async Task Expired_session_expires_the_connection_and_reads_none_of_its_other_accounts()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        var id = await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Courant A"), BankingApi.Account("uid-2", "hash-2", null, "Livret A", "SVGS"));
        app.Banks.BalanceFailures[("session-code-a", "uid-1")] = "enablebanking.EXPIRED_SESSION";
        app.Banks.BalanceFailures[("session-code-a", "uid-2")] = "enablebanking.EXPIRED_SESSION";

        var run = await NightAsync(app, clock, D(8, 13));

        Assert.Single(app.Banks.BalanceCalls);
        Assert.Equal((1, 0, "succeeded"), (run.Connections, run.Accounts, run.Outcome));
        await using var db = Db(app);
        Assert.Equal("expired", (await db.Connections.SingleAsync(c => c.Id == id, cancellationToken: TestContext.Current.CancellationToken)).Status);
        // An expired connection is not read again.
        await NightAsync(app, clock, D(8, 14));
        Assert.Single(app.Banks.BalanceCalls);
    }

    [Fact]
    public async Task A_rate_limit_skips_the_account_and_keeps_the_connection_active()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        var id = await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Courant A"), BankingApi.Account("uid-2", "hash-2", null, "Livret A", "SVGS"));
        app.Banks.BalanceFailures[("session-code-a", "uid-1")] = "enablebanking.ASPSP_RATE_LIMIT_EXCEEDED";
        Serve(app, "session-code-a", "uid-2", D(8, 13), 10m);

        var run = await NightAsync(app, clock, D(8, 13));

        Assert.Equal((1, 1, 1, null), (run.Accounts, run.Balances, run.Skipped, run.FailureCode));
        await using var db = Db(app);
        var connection = await db.Connections.SingleAsync(c => c.Id == id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(("active", null, 0), (connection.Status, connection.LastErrorCode, connection.ConsecutiveFailures));
    }

    [Fact]
    public async Task Another_error_two_nights_running_counts_two_failures()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        var id = await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        app.Banks.BalanceFailures[("session-code-a", "uid-1")] = "enablebanking.ASPSP_ERROR";

        await NightAsync(app, clock, D(8, 13));
        var run = await NightAsync(app, clock, D(8, 14));

        Assert.Equal("enablebanking.ASPSP_ERROR", run.FailureCode);
        await using var db = Db(app);
        var connection = await db.Connections.SingleAsync(c => c.Id == id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(("error", "enablebanking.ASPSP_ERROR", 2), (connection.Status, connection.LastErrorCode, connection.ConsecutiveFailures));
        // Still read the night after, and back to active once the bank answers.
        Serve(app, "session-code-a", "uid-1", D(8, 15), 10m);
        app.Banks.BalanceFailures.Clear();
        await NightAsync(app, clock, D(8, 15));
        await using var after = Db(app);
        var back = await after.Connections.SingleAsync(c => c.Id == id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(("active", 0), (back.Status, back.ConsecutiveFailures));
    }

    [Fact]
    public async Task Joint_account_is_read_once_and_falls_back()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-a", "hash-a", JointIban, "Joint"));
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-b", "hash-b", JointIban, "Joint"));
        var joint = await app.NewEnvelopeAsync("Joint", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        var account = await AccountAsync(app, app.AdultA, "Joint");
        await AssociateAsync(app, app.AdultA, account, joint);
        Serve(app, "session-code-a", "uid-a", D(8, 31), 100m);
        Serve(app, "session-code-b", "uid-b", D(8, 31), 100m);

        var first = await NightAsync(app, clock, D(8, 31));

        Assert.Equal([("session-code-a", "uid-a")], app.Banks.BalanceCalls.Select(c => (c.SessionId, c.AccountUid)));
        Assert.Equal((2, 1), (first.Connections, first.Accounts));

        // A's consent expires: B's connection reads it, the same night.
        app.Banks.BalanceFailures[("session-code-a", "uid-a")] = "enablebanking.EXPIRED_SESSION";
        Serve(app, "session-code-b", "uid-b", D(9, 1), 120m);
        var second = await NightAsync(app, clock, D(9, 1));

        Assert.Equal([("session-code-a", "uid-a"), ("session-code-b", "uid-b")], app.Banks.BalanceCalls.Skip(1).Select(c => (c.SessionId, c.AccountUid)));
        Assert.Equal(1, second.Accounts);
        // Proposed by B, the creator of the connection that read it, who writes the envelope: not by A, who associated it (R3).
        var proposal = Assert.Single(await BankProposalsAsync(app, app.AdultB));
        Assert.Equal(app.AdultB.Value, proposal.GetProperty("createdByPersonId").GetGuid());
    }

    [Fact]
    public async Task A_joint_account_its_reader_cannot_write_is_valued_as_whoever_associated_it()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-a", "hash-a", JointIban, "Joint"));
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-b", "hash-b", JointIban, "Joint"));
        var ofB = await app.NewEnvelopeAsync("Courant de B", app.AdultB, Visibility.Private);
        var account = await AccountAsync(app, app.AdultB, "Joint");
        await AssociateAsync(app, app.AdultB, account, ofB);
        Serve(app, "session-code-a", "uid-a", D(8, 31), 100m);
        await NightAsync(app, clock, D(8, 31));
        // Nobody left who writes the envelope: skipped, and nothing recorded as done.
        await using (var db = Db(app))
            await db.Accounts.Where(a => a.Id == account).ExecuteUpdateAsync(u => u.SetProperty(a => a.AssociatedByPersonId, (Guid?)null), cancellationToken: TestContext.Current.CancellationToken);

        var skipped = await NightAsync(app, clock, D(9, 1));

        Assert.Equal((1, 0), (skipped.Skipped, skipped.Proposals));
        await using (var db = Db(app))
        {
            Assert.Null((await db.Accounts.SingleAsync(a => a.Id == account, cancellationToken: TestContext.Current.CancellationToken)).LastValuedOn);
            await db.Accounts.Where(a => a.Id == account).ExecuteUpdateAsync(u => u.SetProperty(a => a.AssociatedByPersonId, (Guid?)app.AdultB.Value), cancellationToken: TestContext.Current.CancellationToken);
        }

        var valued = await NightAsync(app, clock, D(9, 2));

        // Read through A's older connection, proposed as B, who associated it; A never sees it.
        Assert.All(app.Banks.BalanceCalls, c => Assert.Equal(("session-code-a", "uid-a"), (c.SessionId, c.AccountUid)));
        Assert.Equal((1, 0), (valued.Proposals, valued.Skipped));
        var proposal = Assert.Single(await BankProposalsAsync(app, app.AdultB));
        Assert.Equal(("2026-08-31", app.AdultB.Value), (proposal.GetProperty("subjectKey").GetString(), proposal.GetProperty("createdByPersonId").GetGuid()));
        Assert.Empty(await BankProposalsAsync(app, app.AdultA));
    }

    [Fact]
    public async Task A_renewed_connection_reads_its_accounts_with_the_new_sessions_uids()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        var id = await BankingApi.ConnectAsync(app, app.AdultA, "code-1", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        var renew = await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/connections/{id}/renew"));
        app.Banks.Sessions["code-2"] = new AuthorizedSession("session-code-2", BankingApi.ValidUntil, [BankingApi.Account("uid-1b", "hash-1", null, "Courant A")]);
        await BankingApi.CallbackAsync(app, app.AdultA, $"state={BankingApi.StateOf(renew.GetProperty("redirectUrl").GetString()!)}&code=code-2");
        Serve(app, "session-code-2", "uid-1b", D(8, 13), 10m);

        Assert.Equal(1, (await NightAsync(app, clock, D(8, 13))).Balances);

        Assert.Equal([("session-code-2", "uid-1b")], app.Banks.BalanceCalls.Select(c => (c.SessionId, c.AccountUid)));
        await using var db = Db(app);
        Assert.Equal("uid-1b", (await db.Sources.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).ProviderUid);
    }

    [Fact]
    public async Task An_account_left_out_of_a_renewal_is_no_longer_read()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        var id = await BankingApi.ConnectAsync(app, app.AdultA, "code-1",
            BankingApi.Account("uid-1", "hash-1", null, "Courant A"), BankingApi.Account("uid-2", "hash-2", null, "Livret A", "SVGS"));
        var renew = await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/connections/{id}/renew"));
        app.Banks.Sessions["code-2"] = new AuthorizedSession("session-code-2", BankingApi.ValidUntil, [BankingApi.Account("uid-1b", "hash-1", null, "Courant A")]);
        await BankingApi.CallbackAsync(app, app.AdultA, $"state={BankingApi.StateOf(renew.GetProperty("redirectUrl").GetString()!)}&code=code-2");
        Serve(app, "session-code-2", "uid-1b", D(8, 13), 10m);

        var run = await NightAsync(app, clock, D(8, 13));

        Assert.Equal(("succeeded", null), (run.Outcome, run.FailureCode));
        Assert.Equal([("session-code-2", "uid-1b")], app.Banks.BalanceCalls.Select(c => (c.SessionId, c.AccountUid)));
        var connection = Assert.Single((await BankingApi.ConnectionsAsync(app, app.AdultA)).EnumerateArray());
        Assert.Equal("active", connection.GetProperty("status").GetString());
        // Kept, as on a deletion, no longer synchronised.
        var livret = Assert.Single((await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray(), a => a.GetProperty("name").GetString() == "Livret A");
        Assert.False(livret.GetProperty("synced").GetBoolean());
    }

    [Fact]
    public async Task Ignored_and_unassociated_accounts_store_balances_but_make_no_valuation()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Libre"), BankingApi.Account("uid-2", "hash-2", null, "Ignoré"));
        var ignored = await AccountAsync(app, app.AdultA, "Ignoré");
        await AssociateAsync(app, app.AdultA, ignored, await app.NewEnvelopeAsync("Courant", app.AdultA, Visibility.Private), auto: true);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{ignored}", new { ignored = true })).StatusCode);
        Serve(app, "session-code-a", "uid-1", D(8, 31), 10m);
        Serve(app, "session-code-a", "uid-2", D(8, 31), 20m);

        await NightAsync(app, clock, D(8, 31));
        var run = await NightAsync(app, clock, D(9, 1));

        // The ignored account is not read; the unassociated one keeps its balances.
        Assert.DoesNotContain(app.Banks.BalanceCalls, c => c.AccountUid == "uid-2");
        Assert.Equal((1, 0, 0), (run.Balances, run.Proposals, run.Written));
        await using var db = Db(app);
        Assert.Equal(1, await db.Balances.CountAsync(cancellationToken: TestContext.Current.CancellationToken)); // one day's balance, of the unassociated account
        Assert.Empty(await BankProposalsAsync(app, app.AdultA));
    }

    [Fact]
    public async Task A_pair_in_two_currencies_is_skipped()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        var envelope = await app.NewEnvelopeAsync("Courant", app.AdultA, Visibility.Private);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        var account = await AccountAsync(app, app.AdultA, "Courant A");
        await AssociateAsync(app, app.AdultA, account, envelope, auto: true);
        // A pair left over from before the rule: the envelope's currency changed under it.
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            await core.Items.Where(i => i.Id == envelope.Value).ExecuteUpdateAsync(s => s.SetProperty(i => i.Currency, "USD"), cancellationToken: TestContext.Current.CancellationToken);
        Serve(app, "session-code-a", "uid-1", D(8, 31), 10m);
        await NightAsync(app, clock, D(8, 31));

        var run = await NightAsync(app, clock, D(9, 1));

        Assert.Equal((1, 0, 1), (run.Balances, run.Written, run.Skipped));
    }

    [Fact]
    public async Task Manual_sync_sends_the_browser_headers_values_today_and_waits_for_a_running_one()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        var envelope = await app.NewEnvelopeAsync("Courant", app.AdultA, Visibility.Private);
        var id = await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-2", "hash-2", null, "Courant B"));
        await AssociateAsync(app, app.AdultA, await AccountAsync(app, app.AdultA, "Courant A"), envelope);
        Serve(app, "session-code-a", "uid-1", D(8, 20), 42m);
        Serve(app, "session-code-b", "uid-2", D(8, 20), 43m);
        clock.Now = At(D(8, 20));
        var browser = new Dictionary<string, string> { ["User-Agent"] = "Navigateur/1.0", ["X-Forwarded-For"] = "203.0.113.7" };

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/sync", new { connectionId = id }, browser);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        // Only A's connection, with A's browser.
        var call = Assert.Single(app.Banks.BalanceCalls);
        Assert.Equal(("uid-1", new PsuHeaders("203.0.113.7", "Navigateur/1.0")), (call.AccountUid, call.Psu));
        Assert.Equal(new PsuHeaders("203.0.113.7", "Navigateur/1.0"), Assert.Single(app.Banks.TransactionCalls).Psu);
        Assert.Equal("2026-08-20", Assert.Single(await BankProposalsAsync(app, app.AdultA)).GetProperty("subjectKey").GetString());
        await using (var db = Db(app))
            Assert.Equal("manual", (await db.SyncRuns.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Trigger);
        // The nightly asks without a browser.
        await NightAsync(app, clock, D(8, 21));
        Assert.All(app.Banks.BalanceCalls.Skip(1), c => Assert.Null(c.Psu));

        await using (var db = Db(app))
        {
            db.SyncRuns.Add(new SyncRunRow { Id = Guid.NewGuid(), StartedAt = clock.Now, Trigger = "schedule", Outcome = "running" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/sync", new { }),
            HttpStatusCode.Conflict, BankingCodes.SyncRunning);
        // Someone else's connection is not found.
        await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultB, HttpMethod.Post, $"{BankingApi.Root}/sync", new { connectionId = id }),
            HttpStatusCode.NotFound, BankingCodes.ConnectionNotFound);
    }

    [Fact]
    public async Task Disabled_sync_does_nothing()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        await BankingApi.SettingsAsync(app, new { bankingEnabled = false });

        var result = await NightlyAsync(app, clock, D(8, 13));

        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal(BankingCodes.Disabled, failure!.Code);
        Assert.Empty(app.Banks.BalanceCalls);
        await using var db = Db(app);
        Assert.False(await db.SyncRuns.AnyAsync(cancellationToken: TestContext.Current.CancellationToken));
        await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/sync", new { }),
            HttpStatusCode.Conflict, BankingCodes.Disabled);
    }

    [Fact]
    public async Task The_run_journal_holds_counts_only_and_is_the_admins()
    {
        var (app, _, _, _) = await MonthsAsync(fixture, auto: false, Visibility.Private);
        await using var _app = app;

        await using (var db = Db(app))
        {
            var row = JsonSerializer.Serialize(await db.SyncRuns.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
            foreach (var secret in new[] { "Banque Alpha", "Livret", "uid-1", "session-code-a", app.AdultA.Value.ToString() })
                Assert.DoesNotContain(secret, row, StringComparison.Ordinal);
        }

        var runs = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/admin/banking/runs")).RootElement;
        Assert.Equal(4, runs.GetArrayLength());
        Assert.Equal(("schedule", "succeeded", 1), (runs[0].GetProperty("trigger").GetString(), runs[0].GetProperty("outcome").GetString(), runs[0].GetProperty("accounts").GetInt32()));
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, "/api/v1/admin/banking/runs")).StatusCode);
    }

    private static ProviderTransaction Line(string reference, LocalDate on, decimal amount, string label = "PRLV SEPA NETFLIX")
        => new(reference, on, null, amount, "EUR", label, null, null);

    [Fact]
    public async Task A_night_stores_the_account_s_booked_transactions()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        Serve(app, "session-code-a", "uid-1", D(8, 12), 10m);
        // A line in another currency than the account's is not stored.
        app.Banks.Transactions[("session-code-a", "uid-1")] =
            [Line("r1", D(8, 10), -13.49m), Line("r2", D(8, 11), 1500m, "VIR ACME"), Line("r3", D(8, 11), -1m) with { Currency = "USD" }];

        var run = await NightAsync(app, clock, D(8, 12));

        Assert.Equal(2, run.Transactions);
        await using var db = Db(app);
        var stored = await db.Transactions.OrderBy(t => t.BookedOn).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([("r1", -13.49m, "api", "unlinked"), ("r2", 1500m, "api", "unlinked")], stored.Select(t => (t.ProviderRef, t.Amount, t.FetchedVia, t.LinkState)));
        Assert.Equal(2, (await db.SyncRuns.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Transactions);
    }

    [Fact]
    public async Task A_second_read_adds_nothing()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        Serve(app, "session-code-a", "uid-1", D(8, 12), 10m);
        app.Banks.Transactions[("session-code-a", "uid-1")] = [Line("r1", D(8, 10), -13.49m), Line("r2", D(8, 11), 5m)];

        Assert.Equal(2, (await NightAsync(app, clock, D(8, 12))).Transactions);
        var second = await NightAsync(app, clock, D(8, 13));

        Assert.Equal((0, "succeeded"), (second.Transactions, second.Outcome));
        await using var db = Db(app);
        Assert.Equal(2, await db.Transactions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_next_read_starts_ten_days_before_the_latest_booked_line()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        Serve(app, "session-code-a", "uid-1", D(8, 12), 10m);
        app.Banks.Transactions[("session-code-a", "uid-1")] = [Line("r1", D(8, 10), -1m), Line("r2", D(8, 11), -2m)];

        await NightAsync(app, clock, D(8, 12));
        await NightAsync(app, clock, D(8, 13));

        Assert.Equal([null, D(8, 1)], app.Banks.TransactionCalls.Select(c => c.DateFrom));
    }

    [Fact]
    public async Task A_later_file_line_leaves_the_read_window_on_the_api_s_lines()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        Serve(app, "session-code-a", "uid-1", D(8, 12), 10m);
        app.Banks.Transactions[("session-code-a", "uid-1")] = [Line("r1", D(8, 10), -1m), Line("r2", D(8, 11), -2m)];
        await NightAsync(app, clock, D(8, 12));
        // A statement's upcoming deferred-card line: the bank's own lines after 08-11 must still be read.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<ITransactionStore>().AddAsync(await AccountAsync(app, app.AdultA, "Courant A"),
                [new NewBankTransaction("f1", D(8, 19), null, -9m, Currency.Eur, "CB A VENIR", null, null, "file")], TestContext.Current.CancellationToken);
        }

        await NightAsync(app, clock, D(8, 13));

        Assert.Equal([null, D(8, 1)], app.Banks.TransactionCalls.Select(c => c.DateFrom));
    }

    [Fact]
    public async Task A_line_the_bank_dates_ahead_starts_the_next_read_from_today()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        Serve(app, "session-code-a", "uid-1", D(8, 12), 10m);
        app.Banks.Transactions[("session-code-a", "uid-1")] = [Line("r1", D(8, 10), -1m), Line("r2", D(8, 30), -2m)];

        await NightAsync(app, clock, D(8, 12));
        await NightAsync(app, clock, D(8, 13));

        Assert.Equal([null, D(8, 3)], app.Banks.TransactionCalls.Select(c => c.DateFrom));
    }

    [Fact]
    public async Task An_empty_entry_reference_is_no_reference()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        Serve(app, "session-code-a", "uid-1", D(8, 12), 10m);
        app.Banks.Transactions[("session-code-a", "uid-1")] =
            [Line("", D(8, 10), -1m, "CB CAFE"), Line("", D(8, 10), -2m, "CB PAIN"), Line(" ", D(8, 11), -3m, "CB JOURNAL")];

        Assert.Equal(3, (await NightAsync(app, clock, D(8, 12))).Transactions);

        await using var db = Db(app);
        Assert.DoesNotContain(await db.Transactions.Select(t => t.ProviderRef).ToListAsync(cancellationToken: TestContext.Current.CancellationToken), r => string.IsNullOrWhiteSpace(r));
    }

    [Fact]
    public async Task With_a_retention_a_read_starts_no_earlier_than_it()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        await BankingApi.SettingsAsync(app, new { bankRetentionMonths = 3 });
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        Serve(app, "session-code-a", "uid-1", D(8, 12), 10m);

        // Nothing stored (all purged, or a first read): not the whole history, purged again at once.
        await NightAsync(app, clock, D(8, 12));

        Assert.Equal([D(5, 12)], app.Banks.TransactionCalls.Select(c => c.DateFrom));
    }

    [Fact]
    public async Task A_card_s_transactions_are_read()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Courant A"), BankingApi.Account("uid-2", "hash-2", null, "Carte A", "CARD"));
        var card = await AccountAsync(app, app.AdultA, "Carte A");
        var checking = await AccountAsync(app, app.AdultA, "Courant A");
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{card}", new { cardOfAccountId = checking })).StatusCode);
        Serve(app, "session-code-a", "uid-1", D(8, 12), 10m);
        Serve(app, "session-code-a", "uid-2", D(8, 12), 0m);
        app.Banks.Transactions[("session-code-a", "uid-2")] = [Line("c1", D(8, 11), -4.2m, "CB BOULANGERIE")];

        var run = await NightAsync(app, clock, D(8, 12));

        Assert.Equal(1, run.Transactions);
        await using var db = Db(app);
        Assert.Equal(card, (await db.Transactions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).AccountId);
    }

    [Fact]
    public async Task A_rate_limited_transactions_call_skips_the_account_quietly()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        var id = await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        Serve(app, "session-code-a", "uid-1", D(8, 12), 10m);
        app.Banks.TransactionFailures[("session-code-a", "uid-1")] = "enablebanking.ASPSP_RATE_LIMIT_EXCEEDED";

        var run = await NightAsync(app, clock, D(8, 12));

        Assert.Equal((1, 1, 0, null), (run.Balances, run.Skipped, run.Transactions, run.FailureCode));
        await using var db = Db(app);
        var connection = await db.Connections.SingleAsync(c => c.Id == id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(("active", null, 0), (connection.Status, connection.LastErrorCode, connection.ConsecutiveFailures));
    }

    [Fact]
    public async Task Ignored_accounts_are_not_read()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        var account = await AccountAsync(app, app.AdultA, "Courant A");
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{account}", new { ignored = true })).StatusCode);
        Serve(app, "session-code-a", "uid-1", D(8, 12), 10m);
        app.Banks.Transactions[("session-code-a", "uid-1")] = [Line("r1", D(8, 10), -1m)];

        await NightAsync(app, clock, D(8, 12));

        Assert.Empty(app.Banks.BalanceCalls);
        Assert.Empty(app.Banks.TransactionCalls);
    }

    [Fact]
    public async Task A_read_cut_by_the_page_cap_keeps_its_lines_and_journals_it_without_touching_the_connection()
    {
        var (app, clock) = await StartAsync(fixture);
        await using var _app = app;
        var id = await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"));
        Serve(app, "session-code-a", "uid-1", D(8, 12), 10m);
        app.Banks.Transactions[("session-code-a", "uid-1")] = [Line("r1", D(8, 10), -1m), Line("r2", D(8, 11), -2m)];
        app.Banks.TransactionsTruncated.Add(("session-code-a", "uid-1"));

        var run = await NightAsync(app, clock, D(8, 12));

        Assert.Equal((2, "failed", "banking.transactions_truncated"), (run.Transactions, run.Outcome, run.FailureCode));
        await using var db = Db(app);
        var connection = await db.Connections.SingleAsync(c => c.Id == id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(("active", null, 0), (connection.Status, connection.LastErrorCode, connection.ConsecutiveFailures));
    }
}
