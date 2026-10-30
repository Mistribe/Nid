using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.Application.Sync;

/// <summary>Sent by the <c>bank-sync-nightly</c> job as the system actor, who is admin: every connection that may still be read.</summary>
public sealed record SyncAllCommand : ICommand<BankSyncRunView>, IAdminOnly;

/// <summary>
/// The « Synchroniser » button (D6): the actor's connections, or the one given, read now with their browser's address and agent, and today
/// valued as well as the month-ends due.
/// </summary>
public sealed record SyncMineCommand(Guid? ConnectionId, PsuHeaders Psu) : ICommand<BankSyncRequested>, IBrowserOnly;

public sealed record GetBankSyncRunsQuery : IQuery<IReadOnlyList<BankSyncRunView>>, IAdminOnly;

/// <summary>The run the button started, finished by the time it answers.</summary>
public sealed record BankSyncRequested(Guid RunId);

[RequestHandler<SyncAllCommand, BankSyncRunView>]
public sealed class SyncAllHandler(
    BankingAccess access, IConnectionStore connections, BankSyncCollector collector, IHouseholdSettings settings, IHouseholdCalendar calendar,
    ITransactionStore transactions)
    : IRequestHandler<SyncAllCommand, BankSyncRunView>
{
    public async ValueTask<Result<BankSyncRunView>> HandleAsync(SyncAllCommand command, CancellationToken ct = default)
    {
        // Retention (D3), counted nowhere visible, runs with banking on even where there is no bank to read: statement files alone fill the table.
        var enabled = await access.EnabledAsync(ct);
        var app = await access.AppIdAsync(ct);
        var run = app.TryGetValue(out var appId)
            ? await collector.SyncAsync(appId!, "schedule", await connections.ActiveAsync(ct), null, ct)
            : BankingAccess.Failed<BankSyncRunView, string>(app);
        if (enabled.IsSuccess && (await settings.ReadAsync(ct)).BankRetentionMonths is { } months)
            await transactions.PurgeBookedBeforeAsync((await calendar.TodayAsync(ct)).PlusMonths(-months), ct);
        return run;
    }
}

[RequestHandler<SyncMineCommand, BankSyncRequested>]
public sealed class SyncMineHandler(BankingAccess access, IConnectionStore connections, BankSyncCollector collector)
    : IRequestHandler<SyncMineCommand, BankSyncRequested>
{
    public async ValueTask<Result<BankSyncRequested>> HandleAsync(SyncMineCommand command, CancellationToken ct = default)
    {
        var app = await access.AppIdAsync(ct);
        if (!app.TryGetValue(out var appId))
            return BankingAccess.Failed<BankSyncRequested, string>(app);

        IReadOnlyList<BankConnection> mine;
        if (command.ConnectionId is { } id)
        {
            var one = await access.MyConnectionAsync(id, ct);
            if (!one.TryGetValue(out var connection))
                return BankingAccess.Failed<BankSyncRequested, BankConnection>(one);
            mine = [connection!];
        }
        else
            mine = await connections.MineAsync(access.Person, ct);

        // An expired or revoked consent has nothing to read until it is renewed.
        var readable = mine.Where(c => c.Status is ConnectionStatus.Active or ConnectionStatus.Error).ToList();
        return (await collector.SyncAsync(appId!, "manual", readable, command.Psu, ct)).Map(run => new BankSyncRequested(run.Id));
    }
}

[RequestHandler<GetBankSyncRunsQuery, IReadOnlyList<BankSyncRunView>>]
public sealed class GetBankSyncRunsHandler(IBankSyncRunLog runs) : IRequestHandler<GetBankSyncRunsQuery, IReadOnlyList<BankSyncRunView>>
{
    public const int RecentRuns = 50;

    public async ValueTask<Result<IReadOnlyList<BankSyncRunView>>> HandleAsync(GetBankSyncRunsQuery query, CancellationToken ct = default)
        => Result.Success(await runs.RecentAsync(RecentRuns, ct));
}
