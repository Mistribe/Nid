using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Nid.Host;

/// <summary>What the reference compose needs from the host (1F1 design §2.3).</summary>
public static class HostingSetup
{
    /// <summary>Docker secrets: one file per setting, `Auth__ClientSecret` becoming `Auth:ClientSecret` (security/infrastructure.md §3).</summary>
    public static void AddSecretFiles(this ConfigurationManager configuration)
        => configuration.AddKeyPerFile(configuration["Secrets:Path"] ?? "/run/secrets", optional: true);

    /// <summary>Keys on a volume, so a restart does not sign everyone out; unset in development, as before.</summary>
    public static void AddNidDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var builder = services.AddDataProtection().SetApplicationName("Nid");
        if (configuration["DataProtection:KeysPath"] is { Length: > 0 } path)
            builder.PersistKeysToFileSystem(new DirectoryInfo(path));
    }

    public static void AddNidHealth(this IServiceCollection services)
        => services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

    /// <summary>Status only, never the exception: these routes are reachable by whoever reaches the proxy.</summary>
    public static void MapNidHealth(this WebApplication app)
    {
        static Task Write(HttpContext context, HealthReport report) => context.Response.WriteAsync(report.Status.ToString());
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = c => c.Tags.Contains("live"), ResponseWriter = Write });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { ResponseWriter = Write });
    }

    private sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
        {
            try
            {
                await using var command = dataSource.CreateCommand("select 1");
                await command.ExecuteScalarAsync(ct);
                return HealthCheckResult.Healthy();
            }
            catch (Exception e) when (e is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException)
            {
                return HealthCheckResult.Unhealthy();
            }
        }
    }
}
