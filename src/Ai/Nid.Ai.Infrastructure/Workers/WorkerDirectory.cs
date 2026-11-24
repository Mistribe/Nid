using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Infrastructure.Persistence;
using UnambitiousFx.Myelin;

namespace Nid.Ai.Infrastructure.Workers;

/// <summary>A worker as the outbound client needs it: where it is and how its certificate is checked.</summary>
internal sealed record WorkerEndpoint(string Host, int Port, WorkerStatus Status, bool ProxyDeclared, string? TlsCertificateSha256);

/// <summary>The workers Nid may connect to: Myelin's registered endpoints (private addresses allowed for exactly these
/// host and port pairs, <c>docs/ai/README.md</c> §3.5) and the TLS pins. A snapshot of the registry, reloaded after any
/// change (<see cref="Invalidate"/>) and at least every minute; a disabled or revoked worker leaves it at once.</summary>
public sealed class WorkerDirectory(IServiceScopeFactory scopes, NodaTime.IClock clock) : IRegisteredEndpoints, IWorkerDirectory
{
    private static readonly NodaTime.Duration MaxAge = NodaTime.Duration.FromMinutes(1);

    private volatile Snapshot? _snapshot;

    public void Invalidate() => _snapshot = null;

    public async ValueTask<bool> IsRegisteredAsync(string host, int port, CancellationToken cancellationToken)
        => (await CurrentAsync(cancellationToken)).Endpoints
            .Any(e => e.Port == port && Domain.WorkerRules.SameHost(e.Host, host));

    /// <summary>The TLS check of every connection to a worker (<see cref="WorkerTlsPins"/>); <paramref name="port"/> when the
    /// connection tells it.</summary>
    public bool ValidateCertificate(string host, int? port, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null)
            return false;

        var endpoints = CurrentAsync(CancellationToken.None).GetAwaiter().GetResult().Endpoints;
        return WorkerTlsPins.Validate(endpoints, host, port, Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData())), errors);
    }

    /// <summary>The port a TLS connection was opened to: Myelin's connect callback hands the TLS layer a socket's stream, which
    /// <see cref="AuthenticatedStream"/> keeps protected; null when it cannot be read.</summary>
    public static int? PortOf(object sender)
        => sender is SslStream ssl && InnerStream?.GetValue(ssl) is System.Net.Sockets.NetworkStream { Socket.RemoteEndPoint: System.Net.IPEndPoint endpoint }
            ? endpoint.Port
            : null;

    private static readonly System.Reflection.PropertyInfo? InnerStream = typeof(AuthenticatedStream).GetProperty(
        "InnerStream", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private async Task<Snapshot> CurrentAsync(CancellationToken ct)
    {
        if (_snapshot is { } current && clock.GetCurrentInstant() - current.At < MaxAge)
            return current;

        await using var scope = scopes.CreateAsyncScope();
        // A registration never paired stops being reachable after an hour (and stops unpinning its host's TLS).
        var now = clock.GetCurrentInstant();
        var pendingSince = now - NodaTime.Duration.FromTimeSpan(Domain.WorkerRules.PendingLifetime);
        var rows = await scope.ServiceProvider.GetRequiredService<AiDbContext>().Workers.AsNoTracking()
            .Where(w => (w.Status == "pending" && w.CreatedAt >= pendingSince) || w.Status == "active")
            .Select(w => new { w.Host, w.Port, w.Status, w.ProxyDeclared, w.TlsCertificateSha256 })
            .ToListAsync(ct);
        var loaded = new Snapshot(now, rows
            .Select(r => new WorkerEndpoint(r.Host, r.Port, Enum.Parse<WorkerStatus>(r.Status, ignoreCase: true), r.ProxyDeclared, r.TlsCertificateSha256))
            .ToList());
        _snapshot = loaded;
        return loaded;
    }

    private sealed record Snapshot(NodaTime.Instant At, IReadOnlyList<WorkerEndpoint> Endpoints);
}

/// <summary>Which certificate a worker may present. Reaching it directly: the certificate pinned at pairing (any pin of
/// that host). Behind a proxy the admin declared: a certificate valid for a public authority. Before pairing: any, since only
/// <c>GET /v1/key</c> is sent and the admin compares its fingerprint; on that registration's own host and port only (#626).
/// The worker's identity is its pinned HPKE key either way; TLS only keeps the channel private.</summary>
internal static class WorkerTlsPins
{
    public static bool Validate(IReadOnlyList<WorkerEndpoint> endpoints, string host, int? port, string certificateSha256, SslPolicyErrors errors)
    {
        var onHost = endpoints.Where(e => Domain.WorkerRules.SameHost(e.Host, host)).ToList();
        // ponytail: a connection that does not tell its port (none today: Myelin's is a socket) keeps the host-wide rule.
        return onHost.Any(e => e.Status == WorkerStatus.Pending && (port is null || e.Port == port))
               || onHost.Any(e => e.ProxyDeclared && errors == SslPolicyErrors.None)
               || onHost.Any(e => !e.ProxyDeclared && e.TlsCertificateSha256 == certificateSha256);
    }
}
