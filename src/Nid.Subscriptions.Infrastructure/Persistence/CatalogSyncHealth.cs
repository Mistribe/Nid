using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;

namespace Nid.Subscriptions.Infrastructure.Persistence;

public sealed class CatalogSyncHealth(ICatalogRunLog runs) : ISyncHealth
{
    public async Task<SyncState?> ReadAsync(CancellationToken ct)
        => SyncState.FromRuns("catalog", (await runs.RecentAsync(100, ct)).Select(r => (r.Status == CatalogRunStatus.Running, r.Status == CatalogRunStatus.Succeeded, r.FinishedAt ?? r.StartedAt)));
}
