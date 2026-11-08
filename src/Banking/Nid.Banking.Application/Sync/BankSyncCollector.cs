using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using Duration = NodaTime.Duration;

namespace Nid.Banking.Application.Sync;

/// <summary>
/// One sync (spec §3.2, §3.3, §4), journaled as <c>PriceCollector</c> journals its runs: whatever goes wrong ends in the journal, and a
/// cancellation of the caller's token closes the run as <see cref="BankingCodes.RunInterrupted"/>. Connections are read oldest first, each
/// in a scope of its own as its creator (<see cref="Actor.BankSync"/>); an account is read once, by the first live connection of the run
/// that sees it (D21). The journal holds counts and codes only.
/// </summary>
public sealed class BankSyncCollector(
    IServiceScopeFactory scopes, IBankSyncRunLog runs, BalanceValuation valuation, IClock clock, ILogger<BankSyncCollector> log)
{
    public const int RetentionDays = 400;

    /// <summary>Late-booked lines: a read starts this many days before the latest stored booking (the store drops the ones it knows).</summary>
    public const int TransactionOverlapDays = 10;

    public const string ProposalKind = "investments.record_valuation";

    /// <summary>The proposal's source, as « À valider » shows it.</summary>
    public const string SourceLabel = "Banque";

    private sealed class Progress
    {
        public int Connections, Accounts, Balances, Proposals, Written, Skipped, Transactions;
        public string? FailureCode;

        /// <summary>The accounts read in this run: a later connection that sees one leaves it.</summary>
        public readonly HashSet<Guid> Read = [];
    }

    /// <param name="psu">The browser's address and agent on a manual sync, <c>null</c> at night.</param>
    public async Task<Result<BankSyncRunView>> SyncAsync(
        string appId, string trigger, IReadOnlyList<BankConnection> connections, PsuHeaders? psu, CancellationToken ct)
    {
        var run = await runs.TryStartAsync(trigger, ct);
        if (run is null)
            return Result.Failure<BankSyncRunView>(new Failure(BankingCodes.SyncRunning, "A bank sync is already running.", null));

        var progress = new Progress();
        string? stopped = null;
        try
        {
            foreach (var connection in connections)
                await SyncConnectionAsync(appId, connection, psu, trigger == "manual", progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            stopped = BankingCodes.RunInterrupted;
        }

        var failure = stopped ?? progress.FailureCode;
        var finished = run with
        {
            FinishedAt = clock.GetCurrentInstant().ToDateTimeOffset(), Outcome = failure is null ? "succeeded" : "failed",
            Connections = progress.Connections, Accounts = progress.Accounts, Balances = progress.Balances, Proposals = progress.Proposals,
            Written = progress.Written, Skipped = progress.Skipped, FailureCode = failure, Transactions = progress.Transactions,
        };
        await runs.FinishAsync(finished, CancellationToken.None); // a cancelled run is still closed
        if (stopped is not null)
            ct.ThrowIfCancellationRequested();
        await runs.PurgeBeforeAsync(clock.GetCurrentInstant() - Duration.FromDays(RetentionDays), CancellationToken.None);
        return Result.Success(finished);
    }

    /// <summary>A connection that throws is journaled and the next one read: one bank's trouble never stops the others'.</summary>
    private async Task SyncConnectionAsync(string appId, BankConnection connection, PsuHeaders? psu, bool manual, Progress progress, CancellationToken ct)
    {
        progress.Connections++;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            services.GetRequiredService<IActorAccessor>().Current = Actor.BankSync(connection.Person);
            await ReadAsync(services, appId, connection, psu, manual, progress, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // The type only: a message may carry a bank's answer.
            log.LogError("A bank connection's sync threw {Exception}", e.GetType().Name);
            progress.FailureCode ??= BankingCodes.RunCrashed;
        }
    }

    private async Task ReadAsync(IServiceProvider services, string appId, BankConnection connection, PsuHeaders? psu, bool manual, Progress progress, CancellationToken ct)
    {
        var connections = services.GetRequiredService<IConnectionStore>();
        var accounts = services.GetRequiredService<IAccountStore>();
        var balances = services.GetRequiredService<IBalanceStore>();
        var client = services.GetRequiredService<IEnableBankingClient>();
        var today = await services.GetRequiredService<IHouseholdCalendar>().TodayAsync(ct);

        // The key ring lost: only a renewal brings the session back.
        if (await connections.SessionIdAsync(connection.Id, ct) is not { } session)
        {
            await connections.SetStatusAsync(connection.Id, ConnectionStatus.Expired, null, ct);
            return;
        }

        // Retention (D3): a read never reaches back to what the night purges again.
        var floor = await services.GetRequiredService<IHouseholdSettings>().ReadAsync(ct) is { BankRetentionMonths: { } months } ? today.PlusMonths(-months) : (LocalDate?)null;
        string? error = null;
        foreach (var account in await accounts.ForConnectionAsync(connection.Id, ct))
        {
            if (account.Ignored || progress.Read.Contains(account.Id))
                continue;
            var uid = (await accounts.SourcesAsync(account.Id, ct)).Single(s => s.Connection == connection.Id).ProviderUid;
            var fetched = await client.BalancesAsync(appId, session, uid, psu, ct);
            if (!fetched.TryGetValue(out var list))
            {
                var code = fetched.TryGetFailure(out var failure) ? failure!.Code : BankingCodes.ProviderError;
                switch (await OnFailureAsync(connections, connection, code, progress, ct))
                {
                    case FailureStep.Stop:
                        return;
                    case FailureStep.Error:
                        error ??= code;
                        continue;
                    default:
                        continue;
                }
            }

            progress.Read.Add(account.Id);
            progress.Accounts++;
            // A balance in another currency than the account's would be stored as the wrong money.
            if (BalancePick.Choose(list!.Where(b => string.Equals(b.Currency, account.Currency.Code, StringComparison.OrdinalIgnoreCase)).ToList()) is var (balance, _))
            {
                await balances.UpsertAsync(new BankBalance(account.Id, balance.ReferenceDate ?? today, balance.Amount, balance.Type, clock.GetCurrentInstant()), ct);
                progress.Balances++;
            }

            var stored = services.GetRequiredService<ITransactionStore>();
            // The bank's own lines set the window: a statement's line (deferred card, a gap filled by an old file) would skip real ones.
            var latest = await stored.LatestBookedOnAsync(account.Id, "api", ct) ?? await stored.LatestBookedOnAsync(account.Id, null, ct);
            var since = latest is { } l ? (l < today ? l : today).PlusDays(-TransactionOverlapDays) : (LocalDate?)null;
            if (floor is { } f && !(since >= f))
                since = f;
            var lines = await client.TransactionsAsync(appId, session, uid, since, psu, ct);
            if (lines.TryGetValue(out var read))
            {
                var booked = read.Lines;
                // A line in another currency than the account's would be stored as the wrong money.
                var mine = booked.Where(t => string.Equals(t.Currency, account.Currency.Code, StringComparison.OrdinalIgnoreCase))
                    .Select(t => new NewBankTransaction(string.IsNullOrWhiteSpace(t.EntryReference) ? null : t.EntryReference, t.BookedOn, t.ValueOn, t.Amount, account.Currency, t.Label, t.Counterparty, t.Remittance, "api"))
                    .ToList();
                progress.Transactions += (await stored.AddAsync(account.Id, mine, ct)).Added;
                // The oldest part of the window may stay unread: journal only, the connection is fine.
                if (read.Truncated)
                    progress.FailureCode ??= BankingCodes.TransactionsTruncated;
            }
            else
            {
                var code = lines.TryGetFailure(out var failure) ? failure!.Code : BankingCodes.ProviderError;
                switch (await OnFailureAsync(connections, connection, code, progress, ct))
                {
                    case FailureStep.Stop:
                        return;
                    case FailureStep.Error:
                        error ??= code;
                        break;
                }
            }

            if (account.Envelope is not null)
            {
                var outcome = await valuation.ValueAsync(services, connection.Person, account, today, manual, ct);
                progress.Proposals += outcome.Proposals;
                progress.Written += outcome.Written;
                progress.Skipped += outcome.Skipped;
            }
        }

        if (error is not null)
        {
            await connections.SetStatusAsync(connection.Id, ConnectionStatus.Error, error, ct);
            progress.FailureCode ??= error;
        }
        await connections.RecordSyncAsync(connection.Id, clock.GetCurrentInstant(), error is not null, ct);
    }

    private enum FailureStep { Stop, Skip, Error }

    /// <summary>A provider failure on an account: expired and revoked end the connection's read, a rate limit skips quietly (tomorrow), the rest is the connection's error.</summary>
    private static async Task<FailureStep> OnFailureAsync(IConnectionStore connections, BankConnection connection, string code, Progress progress, CancellationToken ct)
    {
        switch (EnableBankingErrors.Classify(code))
        {
            case ProviderErrorKind.Expired:
                // Its other accounts wait for a renewal; a later connection that sees them reads them tonight.
                await connections.SetStatusAsync(connection.Id, ConnectionStatus.Expired, null, ct);
                return FailureStep.Stop;
            case ProviderErrorKind.Revoked:
                await connections.SetStatusAsync(connection.Id, ConnectionStatus.Revoked, null, ct);
                return FailureStep.Stop;
            case ProviderErrorKind.RateLimited:
                progress.Skipped++;
                return FailureStep.Skip;
            default:
                return FailureStep.Error;
        }
    }
}
