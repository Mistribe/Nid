using Nid.Platform.Contracts;
using Nid.Platform.Notifications;
using NodaTime;

namespace Nid.Platform.Tests;

/// <summary>Notifications design §5.1: which opened rows go out at once, which wait for a digest, and when a failed send is retried.</summary>
public class OutboxRulesTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 3, 1, 9, 0);

    private static NotificationPreferences Prefs(ActionMode action, InfoMode info)
        => NotificationPreferences.Defaults with { Action = action, Info = info };

    [Theory]
    [InlineData(NotificationLevel.Critical, ActionMode.Immediate, InfoMode.Digest, true, false)]
    [InlineData(NotificationLevel.Critical, ActionMode.Digest, InfoMode.Centre, true, false)]
    [InlineData(NotificationLevel.Action, ActionMode.Immediate, InfoMode.Digest, true, true)]
    [InlineData(NotificationLevel.Action, ActionMode.Digest, InfoMode.Digest, false, true)]
    [InlineData(NotificationLevel.Info, ActionMode.Immediate, InfoMode.Digest, false, true)]
    [InlineData(NotificationLevel.Info, ActionMode.Immediate, InfoMode.Centre, false, false)]
    [InlineData(NotificationLevel.Info, ActionMode.Digest, InfoMode.Centre, false, false)]
    public void Level_and_mode_decide_immediate_or_digest(NotificationLevel level, ActionMode action, InfoMode info, bool immediate, bool digest)
    {
        var prefs = Prefs(action, info);

        Assert.Equal(immediate, OutboxRules.IsImmediate(level, prefs));
        Assert.Equal(digest, OutboxRules.GoesToDigest(level, prefs));
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(1, 5 * 60)]
    [InlineData(2, 30 * 60)]
    [InlineData(3, 2 * 3600)]
    [InlineData(4, 12 * 3600)]
    public void Retries_follow_the_schedule(int attempts, int seconds)
        => Assert.Equal(Now + NodaTime.Duration.FromSeconds(seconds), OutboxRules.NextAttempt(attempts, Now));

    [Theory]
    [InlineData(5)]
    [InlineData(9)]
    public void After_the_last_retry_there_is_none(int attempts)
        => Assert.Null(OutboxRules.NextAttempt(attempts, Now));
}
