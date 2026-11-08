using Nid.Investments.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Investments.Infrastructure.Persistence;

public sealed class PriceSyncHealth(IPriceRunLog runs) : ISyncHealth
{
    public async Task<SyncState?> ReadAsync(CancellationToken ct)
        => SyncState.FromRuns("prices", (await runs.RecentAsync(100, ct)).Select(r => (r.Status == PriceRunStatus.Running, r.Status == PriceRunStatus.Succeeded, r.FinishedAt ?? r.StartedAt)));
}
