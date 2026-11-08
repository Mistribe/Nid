using Nid.Subscriptions.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Host.Jobs;

/// <summary>What <see cref="ICatalogJobTrigger"/> is when the host runs without jobs (<c>Jobs:Enabled=false</c>, as in tests).</summary>
public sealed class JobsDisabledCatalogTrigger : ICatalogJobTrigger
{
    public Task<Result> TriggerSyncAsync(CancellationToken ct)
        => Task.FromResult(Result.Failure(new Failure(CatalogCodes.JobsDisabled, "Scheduled jobs are disabled on this instance.", null)));
}
