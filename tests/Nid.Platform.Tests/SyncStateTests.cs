using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Platform.Tests;

/// <summary>Notifications design §2.3: what a module's run log says, most recent run first.</summary>
public class SyncStateTests
{
    private static readonly Instant Day1 = Instant.FromUtc(2026, 10, 1, 4, 0);
    private static readonly Instant Day2 = Instant.FromUtc(2026, 10, 2, 4, 0);
    private static readonly Instant Day3 = Instant.FromUtc(2026, 10, 3, 4, 0);

    [Fact]
    public void Running_only_is_no_state()
        => Assert.Null(SyncState.FromRuns("fx", [(true, false, Day1)]));

    [Fact]
    public void Latest_succeeded_is_no_failure()
        => Assert.Equal(new SyncState("fx", false, Day2),
            SyncState.FromRuns("fx", [(true, false, Day3), (false, true, Day2), (false, false, Day1)]));

    [Fact]
    public void Failure_after_a_success_names_the_success()
        => Assert.Equal(new SyncState("fx", true, Day1),
            SyncState.FromRuns("fx", [(false, false, Day3), (false, false, Day2), (false, true, Day1)]));

    [Fact]
    public void Failures_only_never_succeeded()
        => Assert.Equal(new SyncState("fx", true, null), SyncState.FromRuns("fx", [(false, false, Day2), (false, false, Day1)]));
}
