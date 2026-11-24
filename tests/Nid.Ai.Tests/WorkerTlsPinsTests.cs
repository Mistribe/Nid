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

        Assert.True(WorkerTlsPins.Validate(endpoints, "worker", 8443, Pin, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(WorkerTlsPins.Validate(endpoints, "worker", 8443, Other, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(WorkerTlsPins.Validate(endpoints, "elsewhere", 8443, Pin, SslPolicyErrors.None));
    }

    [Fact]
    public void Behind_a_declared_proxy_a_publicly_valid_certificate_is_enough()
    {
        WorkerEndpoint[] endpoints = [new("gpu.example.org", 443, WorkerStatus.Active, ProxyDeclared: true, null)];

        Assert.True(WorkerTlsPins.Validate(endpoints, "gpu.example.org", 8443, Other, SslPolicyErrors.None));
        Assert.False(WorkerTlsPins.Validate(endpoints, "gpu.example.org", 8443, Other, SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void A_pending_worker_is_reached_before_anything_is_pinned()
    {
        WorkerEndpoint[] endpoints = [new("worker", 8443, WorkerStatus.Pending, ProxyDeclared: false, null)];

        Assert.True(WorkerTlsPins.Validate(endpoints, "worker", 8443, Other, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void An_ipv6_host_matches_with_or_without_brackets()
    {
        WorkerEndpoint[] endpoints = [new("fd00::1", 8443, WorkerStatus.Active, ProxyDeclared: false, Pin)];

        Assert.True(WorkerTlsPins.Validate(endpoints, "[fd00::1]", 8443, Pin, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(WorkerTlsPins.Validate(endpoints, "fd00::1", 8443, Pin, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void A_pending_registration_unpins_only_its_own_port()
    {
        // #626: an active worker on 8443, a second registration pending on 9443 of the same host.
        WorkerEndpoint[] endpoints =
        [
            new("box", 8443, WorkerStatus.Active, ProxyDeclared: false, Pin),
            new("box", 9443, WorkerStatus.Pending, ProxyDeclared: false, null),
        ];

        Assert.False(WorkerTlsPins.Validate(endpoints, "box", 8443, Other, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(WorkerTlsPins.Validate(endpoints, "box", 8443, Pin, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(WorkerTlsPins.Validate(endpoints, "box", 9443, Other, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public async Task The_tls_callback_reads_the_port_its_connection_was_opened_to()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        await using var ssl = new SslStream(client.GetStream());

        Assert.Equal(port, WorkerDirectory.PortOf(ssl));
        Assert.Null(WorkerDirectory.PortOf(new object()));
    }

    /// <summary>A pin holds for its host, whatever the port: on a host with two workers, either pin is accepted for
    /// both (the worker's identity is its pinned HPKE key either way).</summary>
    [Fact]
    public void Two_workers_on_one_host_are_reached_with_either_pin_and_no_other()
    {
        WorkerEndpoint[] endpoints =
        [
            new("box", 8443, WorkerStatus.Active, ProxyDeclared: false, Pin),
            new("box", 9443, WorkerStatus.Active, ProxyDeclared: false, Other),
        ];

        Assert.True(WorkerTlsPins.Validate(endpoints, "box", 8443, Pin, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(WorkerTlsPins.Validate(endpoints, "box", 8443, Other, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(WorkerTlsPins.Validate(endpoints, "box", 8443, new string('c', 64), SslPolicyErrors.RemoteCertificateChainErrors));
    }
}
