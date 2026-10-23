using Nid.RealEstate.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Host.Jobs;

/// <summary>What <see cref="IIrlJobTrigger"/> is when the host runs without jobs (<c>Jobs:Enabled=false</c>, as in tests).</summary>
public sealed class JobsDisabledIrlTrigger : IIrlJobTrigger
{
    public Task<Result> TriggerAsync(CancellationToken ct)
        => Task.FromResult(Result.Failure(new Failure(IrlCodes.JobsDisabled, "Scheduled jobs are disabled on this instance.", null)));
}
