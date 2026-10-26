using Nid.Core.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Core.Infrastructure.Persistence;

public sealed class FxSyncHealth(IFxRunLog runs) : ISyncHealth
{
    public async Task<SyncState?> ReadAsync(CancellationToken ct)
        => SyncState.FromRuns("fx", (await runs.RecentAsync(100, ct)).Select(r => (r.Status == FxRunStatus.Running, r.Status == FxRunStatus.Succeeded, r.FinishedAt ?? r.StartedAt)));
}
