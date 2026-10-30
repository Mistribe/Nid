using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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
public sealed class BankSyncCollector(IServiceScopeFactory scopes, IBankSyncRunLog runs, IClock clock, ILogger<BankSyncCollector> log)
{
    public const int RetentionDays = 400;

    public const string ProposalKind = "investments.record_valuation";

    /// <summary>The proposal's source, as « À valider » shows it.</summary>
    public const string SourceLabel = "Banque";

    private sealed class Progress
    {
        public int Connections, Accounts, Balances, Proposals, Written, Skipped;
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
            Written = progress.Written, Skipped = progress.Skipped, FailureCode = failure,
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
                switch (EnableBankingErrors.Classify(code))
                {
                    case ProviderErrorKind.Expired:
                        // Its other accounts wait for a renewal; a later connection that sees them reads them tonight.
                        await connections.SetStatusAsync(connection.Id, ConnectionStatus.Expired, null, ct);
                        return;
                    case ProviderErrorKind.Revoked:
                        await connections.SetStatusAsync(connection.Id, ConnectionStatus.Revoked, null, ct);
                        return;
                    case ProviderErrorKind.RateLimited:
                        progress.Skipped++; // tomorrow, with no error shown
                        continue;
                    default:
                        error ??= code;
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
            if (account.Envelope is not null)
                await ValueAsync(services, connection.Person, account, today, manual, progress, ct);
        }

        if (error is not null)
        {
            await connections.SetStatusAsync(connection.Id, ConnectionStatus.Error, error, ct);
            progress.FailureCode ??= error;
        }
        await connections.RecordSyncAsync(connection.Id, clock.GetCurrentInstant(), error is not null, ct);
    }

    /// <summary>
    /// Spec §4: each due date proposed, or written when the account applies its balances; a value a person entered or imported on that date
    /// is left alone and its date kept on the account. <c>last_valued_on</c> moves past what was done, never past a failure. Written as the
    /// reading connection's creator when they write the envelope, else as the person who associated the account (ruling R3); neither: skipped,
    /// nothing recorded as done.
    /// </summary>
    private static async Task ValueAsync(
        IServiceProvider services, PersonId reader, BankAccount account, LocalDate today, bool manual, Progress progress, CancellationToken ct)
    {
        var envelope = account.Envelope!.Value;
        // A registry read: the currency only, whoever reads the envelope.
        if ((await services.GetRequiredService<IItemRegistry>().FindAsync(envelope, ct))?.Currency != account.Currency.Code)
        {
            progress.Skipped++;
            return;
        }

        var policy = services.GetRequiredService<IAccessPolicy>();
        PersonId? writer = null;
        foreach (var candidate in new[] { reader, account.AssociatedBy }.OfType<PersonId>())
            if ((await policy.CheckAsync(candidate, envelope, AccessKind.Write, ct)).IsSuccess)
            {
                writer = candidate;
                break;
            }
        if (writer is not { } person)
        {
            progress.Skipped++;
            return;
        }
        // A click values today in the clicker's name only: the reader of a manual run is who clicked.
        manual &= person == reader;

        var accessor = services.GetRequiredService<IActorAccessor>();
        var saved = accessor.Current;
        accessor.Current = Actor.BankSync(person);
        try
        {
            await ValueAsAsync(services, account, envelope, today, manual, progress, ct);
        }
        finally
        {
            accessor.Current = saved; // the connection's own writes stay its creator's
        }
    }

    private static async Task ValueAsAsync(
        IServiceProvider services, BankAccount account, ItemId envelope, LocalDate today, bool manual, Progress progress, CancellationToken ct)
    {

        var balances = services.GetRequiredService<IBalanceStore>();
        var associatedOn = account.AssociatedOn ?? today;
        var stored = new List<BankBalance>();
        for (var month = (account.LastValuedOn is { } done && done > associatedOn ? done : associatedOn).With(DateAdjusters.EndOfMonth);
             month <= today.With(DateAdjusters.EndOfMonth);
             month = month.PlusDays(1).With(DateAdjusters.EndOfMonth))
            stored.AddRange(await balances.InMonthAsync(account.Id, month, ct));

        var valuations = services.GetRequiredService<IValuations>();
        var proposals = services.GetRequiredService<IProposalSubmitter>();
        LocalDate? valuedOn = null, conflictOn = null;
        foreach (var (on, balanceOn) in ValuationDates.Due(associatedOn, account.LastValuedOn, stored.Select(b => b.AsOf).ToList(), today, manual))
        {
            var balance = stored.Single(b => b.AsOf == balanceOn);
            if (!(await valuations.LatestAsync(envelope, on, ct)).TryGetValue(out var latest) || !Money.Create(balance.Amount, account.Currency).TryGetValue(out var amount))
            {
                progress.Skipped++;
                break;
            }
            if (latest!.Value is { } current && current.AsOf == on && current.Source != FactSource.BankApi)
            {
                progress.Skipped++;
                valuedOn = conflictOn = on;
                continue;
            }

            var note = "Solde bancaire du " + balanceOn.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
                + (BalancePick.IsFallback(balance.BalanceType) ? " (solde disponible)" : "");
            if (account.AutoApplyBalance)
            {
                if (!(await valuations.RecordAsync(new NewValuation(envelope, on, amount, note, FactSource.BankApi), ct)).IsSuccess)
                {
                    progress.Skipped++;
                    break;
                }
                progress.Written++;
            }
            else
            {
                var submitted = await proposals.SubmitAsync(ProposalKind, envelope, Payload(on, amount, note), SourceLabel, ct);
                if (!submitted.TryGetValue(out var created))
                {
                    progress.Skipped++;
                    break;
                }
                if (created)
                    progress.Proposals++;
            }
            valuedOn = on;
            conflictOn = null; // a later date valued: the warning goes
        }

        if (valuedOn is { } last)
            await services.GetRequiredService<IAccountStore>().RecordValuedAsync(account.Id, last, conflictOn, ct);
    }

    /// <summary><c>investments.record_valuation</c>'s payload: <c>{ asOf, amount { amount, currency }, note }</c>.</summary>
    private static JsonElement Payload(LocalDate on, Money amount, string note)
    {
        var body = new JsonObject
        {
            ["asOf"] = on.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["amount"] = new JsonObject { ["amount"] = amount.AmountText, ["currency"] = amount.Currency.Code },
            ["note"] = note,
        };
        using var document = JsonDocument.Parse(body.ToJsonString());
        return document.RootElement.Clone();
    }
}
