using System.Net.Security;
using Nid.Ai.Contracts;
using Nid.Ai.Infrastructure.Workers;

namespace Nid.Ai.Tests;

public sealed class WorkerTlsPinsTests
{
    private static readonly string Pin = new('a', 64);
    private static readonly string Other = new('b', 64);

    [Fact]
    public void A_direct_worker_is_reached_only_with_its_pinned_certificate()
    {
        WorkerEndpoint[] endpoints = [new("worker", 8443, WorkerStatus.Active, ProxyDeclared: false, Pin)];

        Assert.True(WorkerTlsPins.Validate(endpoints, "worker", Pin, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(WorkerTlsPins.Validate(endpoints, "worker", Other, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(WorkerTlsPins.Validate(endpoints, "elsewhere", Pin, SslPolicyErrors.None));
    }

    [Fact]
    public void Behind_a_declared_proxy_a_publicly_valid_certificate_is_enough()
    {
        WorkerEndpoint[] endpoints = [new("gpu.example.org", 443, WorkerStatus.Active, ProxyDeclared: true, null)];

        Assert.True(WorkerTlsPins.Validate(endpoints, "gpu.example.org", Other, SslPolicyErrors.None));
        Assert.False(WorkerTlsPins.Validate(endpoints, "gpu.example.org", Other, SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void A_pending_worker_is_reached_before_anything_is_pinned()
    {
        WorkerEndpoint[] endpoints = [new("worker", 8443, WorkerStatus.Pending, ProxyDeclared: false, null)];

        Assert.True(WorkerTlsPins.Validate(endpoints, "worker", Other, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Two_workers_on_one_host_each_keep_their_own_pin()
    {
        WorkerEndpoint[] endpoints =
        [
            new("box", 8443, WorkerStatus.Active, ProxyDeclared: false, Pin),
            new("box", 9443, WorkerStatus.Active, ProxyDeclared: false, Other),
        ];

        Assert.True(WorkerTlsPins.Validate(endpoints, "box", Pin, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(WorkerTlsPins.Validate(endpoints, "box", Other, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(WorkerTlsPins.Validate(endpoints, "box", new string('c', 64), SslPolicyErrors.RemoteCertificateChainErrors));
    }
}
