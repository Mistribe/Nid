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

    /// <summary>#450: the decide model's guesses, every 5 minutes, not with the 30-second index passes.</summary>
    [Fact]
    public void The_guesses_run_every_five_minutes()
    {
        Assert.True(ModelWorkLoop.Due(60, ModelWorkLoop.GuessEvery));
        Assert.False(ModelWorkLoop.Due(6, ModelWorkLoop.GuessEvery));
    }

    /// <summary>#450: once asked (a calibration done, « Suggérer à nouveau »), at the next tick, once; a due tick takes the request too.</summary>
    [Fact]
    public void The_guesses_run_at_the_next_tick_once_asked()
    {
        var signal = new Nid.Banking.Application.CategoryGuessSignal();
        Assert.False(ModelWorkLoop.GuessDue(7, signal));
        signal.Request();
        signal.Request();
        Assert.True(ModelWorkLoop.GuessDue(7, signal));
        Assert.False(ModelWorkLoop.GuessDue(8, signal));
        signal.Request();
        Assert.True(ModelWorkLoop.GuessDue(60, signal));
        Assert.False(ModelWorkLoop.GuessDue(61, signal));
    }
}
