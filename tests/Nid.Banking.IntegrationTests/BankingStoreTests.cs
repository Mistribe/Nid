using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using Duration = NodaTime.Duration;

namespace Nid.Banking.IntegrationTests;

/// <summary>Bank sync spec §2: connections, accounts found again across sessions (D21), daily balances and the run journal.</summary>
public class BankingStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string JointIban = "FR76 3000 6000 0112 3456 7890 189";

    private static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    private static byte[] State(string state) => SHA256.HashData(Encoding.UTF8.GetBytes(state));

    // The host's fixed clock, which the stores read.
    private static readonly Instant Now = NidApp.GoldenCaseToday.AtMidnight().InUtc().ToInstant();

    private static async Task<Guid> ActiveConnectionAsync(IConnectionStore store, PersonId person, string state)
    {
        var id = await store.CreatePendingAsync(person, "Boursorama", "FR", State(state), Now + Duration.FromMinutes(15), default);
        await store.ActivateAsync(id, $"session-{state}", Now + Duration.FromDays(180), default);
        return id;
    }

    [Fact]
    public async Task A_pending_connection_is_found_once_by_its_state_and_keeps_its_session_encrypted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var validUntil = Instant.FromUtc(2027, 4, 27, 10, 0);

        var id = await store.CreatePendingAsync(app.AdultA, "Boursorama", "FR", State("abc"), Now + Duration.FromMinutes(15), TestContext.Current.CancellationToken);
        var expired = await store.CreatePendingAsync(app.AdultA, "Boursorama", "FR", State("old"), Now - Duration.FromMinutes(1), TestContext.Current.CancellationToken);

        var pending = await store.FindByStateAsync(State("abc"), TestContext.Current.CancellationToken);
        Assert.Equal((id, app.AdultA, "enable_banking", "Boursorama", "FR", ConnectionStatus.Pending),
            (pending!.Id, pending.Person, pending.Source, pending.AspspName, pending.AspspCountry, pending.Status));
        Assert.Null(await store.FindByStateAsync(State("abc"), TestContext.Current.CancellationToken)); // used once
        Assert.Null(await store.FindByStateAsync(State("old"), TestContext.Current.CancellationToken)); // past its 15 minutes
        Assert.Null(await store.FindByStateAsync(State("unknown"), TestContext.Current.CancellationToken));

        await store.ActivateAsync(id, "session-secret-1", validUntil, TestContext.Current.CancellationToken);

        Assert.Equal("session-secret-1", await store.SessionIdAsync(id, TestContext.Current.CancellationToken));
        var row = await scope.ServiceProvider.GetRequiredService<BankingDbContext>().Connections.AsNoTracking().SingleAsync(c => c.Id == id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(row.SessionId);
        Assert.DoesNotContain("session-secret-1", Encoding.UTF8.GetString(row.SessionId));
        var mine = await store.MineAsync(app.AdultA, TestContext.Current.CancellationToken);
        Assert.Equal((ConnectionStatus.Active, validUntil), mine.Where(c => c.Id == id).Select(c => (c.Status, c.ValidUntil!.Value)).Single());
        Assert.Equal([id], (await store.ActiveAsync(TestContext.Current.CancellationToken)).Select(c => c.Id));
        Assert.Empty(await store.MineAsync(app.AdultB, TestContext.Current.CancellationToken));
        Assert.Contains(expired, mine.Select(c => c.Id));

        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var lines = await platform.AuditLog.Where(r => r.Entity == "banking.connection").ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.DoesNotContain("session_id", l.Data));
        Assert.All(lines, l => Assert.DoesNotContain("state_hash", l.Data));
    }

    [Fact]
    public async Task Sync_failures_count_until_a_success_and_a_deleted_connection_keeps_its_accounts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        var id = await ActiveConnectionAsync(connections, app.AdultA, "a");
        var account = Assert.Single(await accounts.UpsertFromSessionAsync(id, [new SessionAccount("uid-1", "hash-1", JointIban, "Compte joint", "CACC", "EUR")], TestContext.Current.CancellationToken));

        await connections.SetStatusAsync(id, ConnectionStatus.Error, "ASPSP_ERROR", TestContext.Current.CancellationToken);
        await connections.RecordSyncAsync(id, Now, failed: true, TestContext.Current.CancellationToken);
        await connections.RecordSyncAsync(id, Now, failed: true, TestContext.Current.CancellationToken);
        var failing = (await connections.MineAsync(app.AdultA, TestContext.Current.CancellationToken)).Single();
        Assert.Equal((ConnectionStatus.Error, "ASPSP_ERROR", 2, (Instant?)null), (failing.Status, failing.LastErrorCode, failing.ConsecutiveFailures, failing.LastSyncedAt));
        Assert.Single(await connections.ActiveAsync(TestContext.Current.CancellationToken)); // still tried every night

        var at = Now;
        await connections.RecordSyncAsync(id, at, failed: false, TestContext.Current.CancellationToken);
        var synced = (await connections.MineAsync(app.AdultA, TestContext.Current.CancellationToken)).Single();
        Assert.Equal((ConnectionStatus.Active, null, 0, (Instant?)at), (synced.Status, synced.LastErrorCode, synced.ConsecutiveFailures, synced.LastSyncedAt));

        await connections.SetStatusAsync(id, ConnectionStatus.Expired, null, TestContext.Current.CancellationToken);
        Assert.Empty(await connections.ActiveAsync(TestContext.Current.CancellationToken));

        await connections.DeleteAsync(id, TestContext.Current.CancellationToken);
        Assert.Empty(await connections.MineAsync(app.AdultA, TestContext.Current.CancellationToken));
        Assert.Empty(await accounts.ForConnectionAsync(id, TestContext.Current.CancellationToken));
        Assert.Equal(account.Id, (await accounts.FindAsync(account.Id, TestContext.Current.CancellationToken))!.Id);
    }

    [Fact]
    public async Task Accounts_are_found_again_by_iban_or_identification_hash()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var store = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        var a = await ActiveConnectionAsync(connections, app.AdultA, "a");

        var first = await store.UpsertFromSessionAsync(a,
        [
            new SessionAccount("uid-1", "hash-joint-a", JointIban, "Compte joint", "CACC", "EUR"),
            new SessionAccount("uid-2", "hash-card", null, "Carte Visa", "CARD", "EUR"),
        ], TestContext.Current.CancellationToken);
        Assert.Equal([("0189", BankAccountKind.Checking), (null, BankAccountKind.Card)], first.Select(x => (x.IbanMasked, x.Kind)));
        Assert.All(first, x => Assert.Equal((Currency.Eur, false, false, (ItemId?)null), (x.Currency, x.AutoApplyBalance, x.Ignored, x.Envelope)));

        // A renewed session gives new uids: the same accounts come back, by IBAN and by identification hash.
        var renewed = await store.UpsertFromSessionAsync(a,
        [
            new SessionAccount("uid-7", "hash-joint-a", "fr7630006000011234567890189", "Compte joint", "CACC", "EUR"),
            new SessionAccount("uid-8", "hash-card", null, "Carte Visa", "CARD", "EUR"),
        ], TestContext.Current.CancellationToken);
        Assert.Equal(first.Select(x => x.Id), renewed.Select(x => x.Id));

        // The other adult links the joint account through their own connection: one account, two sources.
        var b = await ActiveConnectionAsync(connections, app.AdultB, "b");
        var joint = Assert.Single(await store.UpsertFromSessionAsync(b, [new SessionAccount("uid-b", "hash-joint-b", JointIban, "CPT JOINT", "CACC", "EUR")], TestContext.Current.CancellationToken));
        Assert.Equal(first[0].Id, joint.Id);
        Assert.Equal([first[0].Id], (await store.ForConnectionAsync(b, TestContext.Current.CancellationToken)).Select(x => x.Id));
        Assert.Equal(first.Select(x => x.Id).Order(), (await store.ForConnectionAsync(a, TestContext.Current.CancellationToken)).Select(x => x.Id).Order());
    }

    [Fact]
    public async Task An_envelope_takes_one_account()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var store = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        var a = await ActiveConnectionAsync(connections, app.AdultA, "a");
        var both = await store.UpsertFromSessionAsync(a,
        [
            new SessionAccount("uid-1", "hash-1", null, "Courant", "CACC", "EUR"),
            new SessionAccount("uid-2", "hash-2", null, "Livret", "SVGS", "EUR"),
        ], TestContext.Current.CancellationToken);
        var envelope = new ItemId(Guid.NewGuid());
        var on = new LocalDate(2026, 10, 29);

        Assert.True((await store.UpdateAsync(both[0] with { Envelope = envelope, AutoApplyBalance = true, AssociatedOn = on }, TestContext.Current.CancellationToken)).IsSuccess);
        var taken = await store.UpdateAsync(both[1] with { Envelope = envelope }, TestContext.Current.CancellationToken);

        Assert.True(taken.TryGetFailure(out var failure));
        Assert.Equal(BankingCodes.EnvelopeTaken, failure!.Code);
        var associated = await store.ForEnvelopeAsync(envelope, TestContext.Current.CancellationToken);
        Assert.Equal((both[0].Id, true, (LocalDate?)on), (associated!.Id, associated.AutoApplyBalance, associated.AssociatedOn));
    }

    [Fact]
    public async Task A_settings_update_keeps_what_the_sync_wrote_meanwhile()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        var a = await ActiveConnectionAsync(scope.ServiceProvider.GetRequiredService<IConnectionStore>(), app.AdultA, "a");
        var account = (await store.UpsertFromSessionAsync(a, [new SessionAccount("uid-1", "hash-1", null, "Courant", "CACC", "EUR")], TestContext.Current.CancellationToken))[0];
        var stale = (await store.FindAsync(account.Id, TestContext.Current.CancellationToken))!;
        var on = new LocalDate(2026, 8, 31);

        await store.RecordValuedAsync(account.Id, on, on, TestContext.Current.CancellationToken);
        Assert.True((await store.UpdateAsync(stale with { Ignored = true }, TestContext.Current.CancellationToken)).IsSuccess);

        var after = (await store.FindAsync(account.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal((true, (LocalDate?)on, (LocalDate?)on), (after.Ignored, after.BalanceConflictOn, after.LastValuedOn));
        // A later date valued without a conflict clears the one shown on the account.
        await store.RecordValuedAsync(account.Id, on.PlusDays(30), null, TestContext.Current.CancellationToken);
        Assert.Equal(((LocalDate?)null, (LocalDate?)on.PlusDays(30)), ((await store.FindAsync(account.Id, TestContext.Current.CancellationToken))!.BalanceConflictOn, (await store.FindAsync(account.Id, TestContext.Current.CancellationToken))!.LastValuedOn));
    }

    [Fact]
    public async Task A_balance_of_the_same_day_replaces_the_earlier_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        var balances = scope.ServiceProvider.GetRequiredService<IBalanceStore>();
        var a = await ActiveConnectionAsync(connections, app.AdultA, "a");
        var account = Assert.Single(await accounts.UpsertFromSessionAsync(a, [new SessionAccount("uid-1", "hash-1", null, "Courant", "CACC", "EUR")], TestContext.Current.CancellationToken)).Id;

        await balances.UpsertAsync(new BankBalance(account, new LocalDate(2026, 9, 30), 900m, "CLBD", Now), TestContext.Current.CancellationToken);
        await balances.UpsertAsync(new BankBalance(account, new LocalDate(2026, 10, 15), 1000m, "CLAV", Now), TestContext.Current.CancellationToken);
        await balances.UpsertAsync(new BankBalance(account, new LocalDate(2026, 10, 15), 1234.56m, "CLBD", Now), TestContext.Current.CancellationToken);
        await balances.UpsertAsync(new BankBalance(account, new LocalDate(2026, 10, 28), 1300m, "CLBD", Now), TestContext.Current.CancellationToken);

        var october = await balances.InMonthAsync(account, new LocalDate(2026, 10, 31), TestContext.Current.CancellationToken);
        Assert.Equal([(new LocalDate(2026, 10, 15), 1234.56m, "CLBD"), (new LocalDate(2026, 10, 28), 1300m, "CLBD")],
            october.Select(x => (x.AsOf, x.Amount, x.BalanceType)));
    }

    [Fact]
    public async Task The_run_log_starts_one_run_at_a_time_and_keeps_counts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var log = scope.ServiceProvider.GetRequiredService<IBankSyncRunLog>();

        var first = await log.TryStartAsync("schedule", TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        Assert.Null(await log.TryStartAsync("manual", TestContext.Current.CancellationToken));
        Assert.True(await log.IsRunningAsync(TestContext.Current.CancellationToken));

        await log.FinishAsync(first with
        {
            Outcome = "succeeded", FinishedAt = first.StartedAt, Connections = 2, Accounts = 3, Balances = 3, Proposals = 1, Written = 1, Skipped = 1,
        }, TestContext.Current.CancellationToken);
        Assert.False(await log.IsRunningAsync(TestContext.Current.CancellationToken));
        var second = await log.TryStartAsync("manual", TestContext.Current.CancellationToken);
        Assert.NotNull(second);

        var recent = await log.RecentAsync(5, TestContext.Current.CancellationToken);
        Assert.Equal([second.Id, first.Id], recent.Select(r => r.Id));
        Assert.Equal(("schedule", "succeeded", 2, 3, 3, 1, 1, 1, (string?)null),
            (recent[1].Trigger, recent[1].Outcome, recent[1].Connections, recent[1].Accounts, recent[1].Balances, recent[1].Proposals, recent[1].Written,
                recent[1].Skipped, recent[1].FailureCode));
        Assert.Equal(0, await log.PurgeBeforeAsync(Instant.FromUtc(2000, 1, 1, 0, 0), TestContext.Current.CancellationToken));
    }
}
