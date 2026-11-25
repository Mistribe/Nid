using Nid.Host.Jobs;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>#644: the loop's slower passes come round by ticks of <see cref="ModelWorkLoop.JobsEvery"/>, never by reading the clock.</summary>
public class ModelWorkLoopTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(11, false)]
    [InlineData(12, true)] // a minute of 5-second ticks
    [InlineData(24, true)]
    public void The_workers_pass_runs_every_minute(long tick, bool due) => Assert.Equal(due, ModelWorkLoop.Due(tick, ModelWorkLoop.WorkersEvery));

    [Fact]
    public void The_purge_runs_every_hour()
    {
        Assert.True(ModelWorkLoop.Due(720, ModelWorkLoop.PurgeEvery));
        Assert.False(ModelWorkLoop.Due(719, ModelWorkLoop.PurgeEvery));
    }
}
