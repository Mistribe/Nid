using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Serilog;
using Serilog.Debugging;
using Serilog.Extensions.Logging;

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

    /// <summary>
    /// Rotated files next to the console, off without a path. A folder that cannot be written is reported, never fatal:
    /// the logs must not be what keeps Nid down.
    /// </summary>
    public static void AddNidFileLogs(this ILoggingBuilder logging, IConfiguration configuration)
    {
        if (configuration["FileLogs:Path"] is not { Length: > 0 } path) return;
        try
        {
            Directory.CreateDirectory(path);
            using var probe = File.Create(Path.Combine(path, ".write-test"), 1, FileOptions.DeleteOnClose);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"File logs are off: {path} cannot be written ({e.Message}).");
            return;
        }

        // A sink's own failures (a disk gone read-only later on) go nowhere otherwise.
        SelfLog.Enable(Console.Error);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.File(
                Path.Combine(path, "nid-.log"),
                outputTemplate: "{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: configuration.GetValue("FileLogs:MaxFileSizeMb", 50L) * 1024 * 1024,
                retainedFileCountLimit: configuration.GetValue("FileLogs:RetainedFiles", 30),
                retainedFileTimeLimit: TimeSpan.FromDays(configuration.GetValue("FileLogs:RetainedDays", 14)))
            .CreateLogger();
        // Not AddSerilog: its provider-wide Trace rule overrides Logging:LogLevel, and the files must hold no more than the
        // console. Built by a factory so the container disposes it, which closes the file.
        logging.Services.AddSingleton<ILoggerProvider>(_ => new SerilogLoggerProvider(logger, dispose: true));
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
