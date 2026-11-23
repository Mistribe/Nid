using NodaTime;
using Nid.Platform.Contracts;
using Nid.Core.Contracts;

namespace Nid.Core.Infrastructure.Persistence;

public sealed class CpiSyncHealth(ICpiRunLog runs) : ISyncHealth
{
    public async Task<SyncState?> ReadAsync(CancellationToken ct)
        => SyncState.FromRuns("inflation", (await runs.RecentAsync(100, ct)).Select(r => (r.Status == "running", r.Status == "succeeded", r.FinishedAt ?? r.StartedAt)));
}
