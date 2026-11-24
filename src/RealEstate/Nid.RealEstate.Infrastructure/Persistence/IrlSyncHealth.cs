using Nid.RealEstate.Application;
using NodaTime;
using Nid.Platform.Contracts;

namespace Nid.RealEstate.Infrastructure.Persistence;

public sealed class IrlSyncHealth(IIrlRunLog runs) : ISyncHealth
{
    public async Task<SyncState?> ReadAsync(CancellationToken ct)
        => SyncState.FromRuns("irl", (await runs.RecentAsync(100, ct)).Select(r => (r.Status == "running", r.Status == "succeeded", r.FinishedAt ?? r.StartedAt)));
}
