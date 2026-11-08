using System.Reflection;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Nid.Platform.Admin;
using Npgsql;

namespace Nid.Host.Admin;

/// <summary>Registered as an instance when the services are built, so <see cref="At"/> is the process start.</summary>
public sealed class ProcessStart
{
    public DateTimeOffset At { get; } = DateTimeOffset.UtcNow;
}

public interface IIdentityProviderProbe
{
    Task<bool> IsReachableAsync(CancellationToken ct);
}

/// <summary>
/// The OIDC handler's discovery document: served from its cache when it has one, so this does not hit the identity
/// provider on every call; "unreachable" when no document can be had within 3 seconds.
/// </summary>
public sealed class OidcIdentityProviderProbe(IOptionsMonitor<OpenIdConnectOptions> options) : IIdentityProviderProbe
{
    public async Task<bool> IsReachableAsync(CancellationToken ct)
    {
        var manager = options.Get(OpenIdConnectDefaults.AuthenticationScheme).ConfigurationManager;
        if (manager is null)
            return false;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await manager.GetConfigurationAsync(timeout.Token);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}

public sealed class SystemStatusSource(NpgsqlDataSource dataSource, IIdentityProviderProbe identityProvider, ProcessStart process)
    : ISystemStatusSource
{
    private static readonly string Version =
        typeof(SystemStatusSource).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public async Task<SystemStatusView> ReadAsync(CancellationToken ct)
    {
        long? size = null;
        try
        {
            await using var command = dataSource.CreateCommand("select pg_database_size(current_database())");
            size = (long)(await command.ExecuteScalarAsync(ct))!;
        }
        catch (NpgsqlException)
        {
            // Reported as unhealthy below; the admin page is where an outage should be visible.
        }

        var reachable = await identityProvider.IsReachableAsync(ct);
        return new SystemStatusView(
            Version, process.At,
            new SystemHealthView(size is null ? "unhealthy" : "healthy", reachable ? "healthy" : "unhealthy"),
            size);
    }
}
