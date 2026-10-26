using System.Text.Json;
using System.Text.Json.Serialization;
using NodaTime;

namespace Nid.Platform.Notifications;

/// <summary>Bound from <c>Nid:Backup</c>; no <see cref="StatusPath"/> means no backup signal.</summary>
public sealed class BackupOptions
{
    public string? StatusPath { get; set; }

    /// <summary><c>NID_BACKUP_MAX_AGE</c>'s default.</summary>
    public int MaxAgeHours { get; set; } = 26;
}

/// <summary><paramref name="Reason"/>: the failed step, <c>too_old</c>, <c>remote</c> or <c>unreadable</c>.</summary>
public sealed record BackupState(bool Healthy, string? Reason, Instant? LastSuccessAt);

/// <summary>The rule of <c>nid-backup status</c> (deploy/backup/lib.sh <c>health_check</c>) over its <c>status.json</c>.</summary>
public static partial class BackupStatus
{
    private sealed record Status(
        [property: JsonPropertyName("last_result")] string? LastResult,
        [property: JsonPropertyName("remote_result")] string? RemoteResult,
        [property: JsonPropertyName("last_success_at")] long? LastSuccessAt);

    [JsonSerializable(typeof(Status))]
    private sealed partial class StatusJson : JsonSerializerContext;

    /// <summary><c>null</c>: no signal (no file, or a new installation whose file time is unknown).</summary>
    public static BackupState? Evaluate(string? json, Instant now, Instant? fileWrittenAt, int maxAgeHours)
    {
        if (json is null)
            return null;
        Status? status;
        try
        {
            status = JsonSerializer.Deserialize(json, StatusJson.Default.Status);
        }
        catch (JsonException)
        {
            status = null;
        }
        if (status?.LastResult is null)
            return new BackupState(false, "unreadable", null);

        var success = status.LastSuccessAt is { } epoch ? Instant.FromUnixTimeSeconds(epoch) : (Instant?)null;
        if (status.LastResult is not ("ok" or "none"))
            return new BackupState(false, status.LastResult, success);
        // A new installation has no success yet: its age counts from the file (the script counts it from the service start).
        if ((success ?? fileWrittenAt) is not { } since)
            return null;
        if (now - since > Duration.FromHours(maxAgeHours))
            return new BackupState(false, "too_old", success);
        if (status.RemoteResult is "failed" or "unreachable")
            return new BackupState(false, "remote", success);
        return new BackupState(true, null, success);
    }
}
