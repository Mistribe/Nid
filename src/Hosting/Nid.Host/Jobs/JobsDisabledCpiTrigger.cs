using Nid.Core.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Host.Jobs;

/// <summary>What <see cref="ICpiJobTrigger"/> is when the host runs without jobs (<c>Jobs:Enabled=false</c>, as in tests).</summary>
public sealed class JobsDisabledCpiTrigger : ICpiJobTrigger
{
    public Task<Result> TriggerAsync(CancellationToken ct)
        => Task.FromResult(Result.Failure(new Failure(InflationCodes.JobsDisabled, "Scheduled jobs are disabled on this instance.", null)));
}
