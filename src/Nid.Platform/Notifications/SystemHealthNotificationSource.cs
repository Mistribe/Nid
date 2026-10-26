using Microsoft.Extensions.Logging;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Platform.Notifications;

/// <summary>
/// Notifications design §2.3: the backup and the module syncs, for the admins. Item-less signals: nothing here is private.
/// A run log that cannot be read is logged and skipped, so it never hides the backup or another sync.
/// </summary>
public sealed partial class SystemHealthNotificationSource(
    IPersonRegistry persons, IEnumerable<ISyncHealth> syncs, BackupOptions backup, IClock clock, ILogger<SystemHealthNotificationSource> logger) : INotificationSource
{
    public const string BackupFailed = "backup_failed";
    public const string SyncFailed = "sync_failed";

    public string Name => "health";

    public IReadOnlyList<NotificationKind> Kinds { get; } =
    [
        new(BackupFailed, NotificationFamilies.Health, NotificationLevel.Critical, "platform", null),
        new(SyncFailed, NotificationFamilies.Health, NotificationLevel.Info, "platform", null),
    ];

    public async Task<Result<IReadOnlyList<Signal>>> SignalsAsync(PersonId reader, LocalDate today, LocalDate horizon, CancellationToken ct)
    {
        var signals = new List<Signal>();
        if (await persons.FindAsync(reader, ct) is not { IsAdmin: true })
            return Result.Success<IReadOnlyList<Signal>>(signals);

        if (BackupSignal() is { } bad)
            signals.Add(bad);
        foreach (var sync in syncs)
        {
            try
            {
                if (await sync.ReadAsync(ct) is { LastFinishedFailed: true } state)
                    signals.Add(new Signal(SignalKeys.Household(SyncFailed, $"{state.Source}:{(state.LastSucceededOn is { } on ? $"{on:yyyy-MM-dd}" : "never")}"),
                        SyncFailed, [], null, new Dictionary<string, string> { ["source"] = state.Source }));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                LogSyncUnreadable(e, sync.GetType().Name);
            }
        }
        return Result.Success<IReadOnlyList<Signal>>(signals);
    }

    private Signal? BackupSignal()
    {
        if (string.IsNullOrWhiteSpace(backup.StatusPath) || !File.Exists(backup.StatusPath))
            return null;
        string json;
        Instant? written;
        try
        {
            json = File.ReadAllText(backup.StatusPath);
            written = Instant.FromDateTimeUtc(File.GetLastWriteTimeUtc(backup.StatusPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            json = "";
            written = null;
        }
        if (BackupStatus.Evaluate(json, clock.GetCurrentInstant(), written, backup.MaxAgeHours) is not { Healthy: false } state)
            return null;
        var since = state.LastSuccessAt is { } at ? at.InUtc().ToString("yyyy-MM-ddTHH:mm", null) : "never";
        var parameters = new Dictionary<string, string> { ["reason"] = state.Reason! };
        if (state.Reason == "too_old")
            parameters["hours"] = backup.MaxAgeHours.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new Signal(SignalKeys.Household(BackupFailed, since), BackupFailed, [], null, parameters);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The sync health of {Source} could not be read; it is skipped.")]
    private partial void LogSyncUnreadable(Exception e, string source);
}
