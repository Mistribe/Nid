using Nid.Ai.Contracts;
using Nid.Ai.Domain;

namespace Nid.Ai.Tests;

public sealed class WorkerRulesTests
{
    [Theory]
    [InlineData(WorkerStatus.Pending, null)]
    [InlineData(WorkerStatus.Active, AiCodes.WorkerNotPending)]
    [InlineData(WorkerStatus.Disabled, AiCodes.WorkerNotPending)]
    [InlineData(WorkerStatus.Revoked, AiCodes.WorkerRevoked)]
    public void Only_a_pending_worker_is_paired(WorkerStatus status, string? refusal)
        => Assert.Equal(refusal, WorkerRules.Pair(status));

    [Theory]
    [InlineData(WorkerStatus.Active, null)]
    [InlineData(WorkerStatus.Pending, AiCodes.WorkerNotActive)]
    [InlineData(WorkerStatus.Disabled, AiCodes.WorkerNotActive)]
    [InlineData(WorkerStatus.Revoked, AiCodes.WorkerRevoked)]
    public void Only_an_active_worker_is_disabled_called_or_rotated(WorkerStatus status, string? refusal)
    {
        Assert.Equal(refusal, WorkerRules.Disable(status));
        Assert.Equal(refusal, WorkerRules.Call(status));
        Assert.Equal(refusal, WorkerRules.RotateKey(status));
    }

    [Theory]
    [InlineData(WorkerStatus.Disabled, null)]
    [InlineData(WorkerStatus.Active, AiCodes.WorkerNotDisabled)]
    [InlineData(WorkerStatus.Revoked, AiCodes.WorkerRevoked)]
    public void Only_a_disabled_worker_is_enabled(WorkerStatus status, string? refusal)
        => Assert.Equal(refusal, WorkerRules.Enable(status));

    [Theory]
    [InlineData(WorkerStatus.Pending, null)]
    [InlineData(WorkerStatus.Active, null)]
    [InlineData(WorkerStatus.Disabled, null)]
    [InlineData(WorkerStatus.Revoked, AiCodes.WorkerRevoked)]
    public void Revocation_is_final(WorkerStatus status, string? refusal)
        => Assert.Equal(refusal, WorkerRules.Revoke(status));

    [Theory]
    [InlineData("GPU box", null)]
    [InlineData("  ", AiCodes.InvalidName)]
    [InlineData("a name far longer than sixty characters, which is more than any list shows", AiCodes.InvalidName)]
    public void A_name_is_one_to_sixty_characters(string name, string? refusal)
        => Assert.Equal(refusal, WorkerRules.Name(name));

    [Theory]
    [InlineData("https://worker:8443", null)]
    [InlineData("https://gpu.example.org", null)]
    [InlineData("http://worker:8443", AiCodes.InvalidUrl)]
    [InlineData("https://worker:8443/path", AiCodes.InvalidUrl)]
    [InlineData("https://user:secret@worker:8443", AiCodes.InvalidUrl)]
    [InlineData("worker:8443", AiCodes.InvalidUrl)]
    public void A_worker_url_is_https_host_and_port_only(string url, string? refusal)
        => Assert.Equal(refusal, WorkerRules.Url(url, out _));

    [Theory]
    [InlineData("https://[fd00::1]:8443", "fd00::1", 8443)]
    [InlineData("https://[fd7a:115c:a1e0::1]", "fd7a:115c:a1e0::1", 443)]
    [InlineData("https://Worker:8443", "worker", 8443)]
    public void A_worker_url_keeps_its_host_and_port(string url, string host, int port)
    {
        Assert.Null(WorkerRules.Url(url, out var parsed));
        Assert.Equal(host, WorkerRules.Host(parsed!));
        Assert.Equal(port, parsed!.Port);
        Assert.Equal(parsed, WorkerRules.ToUrl(host, port));
    }

    [Fact]
    public void A_rotation_keeps_the_previous_key_for_a_day()
        => Assert.Equal(TimeSpan.FromHours(24), WorkerRules.KeyOverlap);
}
