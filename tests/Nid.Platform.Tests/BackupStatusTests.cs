using Nid.Platform.Notifications;
using NodaTime;

namespace Nid.Platform.Tests;

/// <summary>The rule of <c>nid-backup status</c> (deploy/backup/lib.sh health_check), read from <c>status.json</c>.</summary>
public class BackupStatusTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 26, 12, 0);

    private static string Json(string result, string remote, Instant? success)
        => $$"""{"last_result":"{{result}}","remote_result":"{{remote}}","last_snapshot":"abc","last_success_at":{{(success is { } s ? s.ToUnixTimeSeconds().ToString() : "null")}}}""";

    private static BackupState? Eval(string? json, Instant? written = null) => BackupStatus.Evaluate(json, Now, written, 26);

    [Fact]
    public void Ok_and_fresh_is_healthy()
    {
        var state = Eval(Json("ok", "ok", Now - Duration.FromHours(3)))!;
        Assert.True(state.Healthy);
        Assert.Equal(Now - Duration.FromHours(3), state.LastSuccessAt);
    }

    [Fact]
    public void A_failed_step_is_the_reason()
        => Assert.Equal(new BackupState(false, "dump", Now - Duration.FromHours(3)), Eval(Json("dump", "none", Now - Duration.FromHours(3))));

    [Fact]
    public void A_success_older_than_the_limit_is_too_old()
        => Assert.Equal("too_old", Eval(Json("ok", "none", Now - Duration.FromHours(27)))!.Reason);

    [Fact]
    public void An_unreachable_or_failed_remote_is_unhealthy()
    {
        Assert.Equal("remote", Eval(Json("ok", "unreachable", Now - Duration.FromHours(1)))!.Reason);
        Assert.Equal("remote", Eval(Json("ok", "failed", Now - Duration.FromHours(1)))!.Reason);
    }

    [Fact]
    public void No_remote_is_healthy()
        => Assert.True(Eval(Json("ok", "none", Now - Duration.FromHours(1)))!.Healthy);

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"last_result\":\"ok\",\"remote_")]
    [InlineData("null")]
    [InlineData("[1]")]
    [InlineData("")]
    public void Garbage_is_unreadable(string json)
        => Assert.Equal(new BackupState(false, "unreadable", null), Eval(json));

    [Fact]
    public void A_new_installation_is_healthy_for_its_first_hours()
    {
        Assert.True(Eval(Json("ok", "none", null), Now - Duration.FromHours(2))!.Healthy);
        Assert.Equal("too_old", Eval(Json("ok", "none", null), Now - Duration.FromHours(30))!.Reason);
    }

    [Fact]
    public void No_json_is_no_signal()
        => Assert.Null(Eval(null));
}
