using Nid.Core.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Host.Jobs;

/// <summary>What <see cref="IFxJobTrigger"/> is when the host runs without jobs (<c>Jobs:Enabled=false</c>, as in tests).</summary>
public sealed class JobsDisabledFxTrigger : IFxJobTrigger
{
    public Task<Result> TriggerBackfillAsync(CancellationToken ct)
        => Task.FromResult(Result.Failure(new Failure(FxCodes.JobsDisabled, "Scheduled jobs are disabled on this instance.", null)));
}
