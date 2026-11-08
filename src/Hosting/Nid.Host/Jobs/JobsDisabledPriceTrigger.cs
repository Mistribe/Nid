using Nid.Investments.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Host.Jobs;

/// <summary>What <see cref="IPriceJobTrigger"/> is when the host runs without jobs (<c>Jobs:Enabled=false</c>, as in tests).</summary>
public sealed class JobsDisabledPriceTrigger : IPriceJobTrigger
{
    public Task<Result> TriggerAsync(CancellationToken ct)
        => Task.FromResult(Result.Failure(new Failure(PriceCodes.JobsDisabled, "Scheduled jobs are disabled on this instance.", null)));
}
