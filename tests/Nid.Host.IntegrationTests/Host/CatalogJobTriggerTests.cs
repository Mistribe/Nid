using Nid.Host.Jobs;
using Nid.Subscriptions.Contracts;
using Quartz;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>Final review H2: a scheduler that cannot take the request answers a coded failure, never a 500.</summary>
public class CatalogJobTriggerTests
{
    private sealed class FailingSchedulerFactory : ISchedulerFactory
    {
        public ValueTask<List<IScheduler>> GetAllSchedulers(CancellationToken cancellationToken = default)
            => throw new SchedulerException("The scheduler has been shut down.");

        public ValueTask<IScheduler> GetScheduler(CancellationToken cancellationToken = default)
            => throw new SchedulerException("The scheduler has been shut down.");

        public ValueTask<IScheduler?> LookupScheduler(string schedName, CancellationToken cancellationToken = default)
            => throw new SchedulerException("The scheduler has been shut down.");
    }

    [Fact]
    public async Task A_scheduler_failure_is_a_coded_failure()
    {
        var triggered = await new QuartzCatalogJobTrigger(new FailingSchedulerFactory()).TriggerSyncAsync(default);

        Assert.True(triggered.TryGetFailure(out var failure));
        Assert.Equal(CatalogCodes.JobsDisabled, failure!.Code);
    }
}
