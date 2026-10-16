using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nid.Platform.Audit;
using Nid.Platform.Contracts;
using NodaTime;
using Npgsql;
using NpgsqlTypes;

namespace Nid.Platform.Infrastructure.Audit;

/// <summary><see cref="ISecurityEvents"/> on a connection of its own from the pool (spec U10, U11).</summary>
public sealed partial class SecurityEventWriter(
    NpgsqlDataSource dataSource, IActorAccessor actors, AuditRequest request, IClock clock, ILogger<SecurityEventWriter> logger)
    : ISecurityEvents
{
    public Task<bool> RecordAsync(SecurityEvent securityEvent, CancellationToken ct)
        => RecordAsync(securityEvent, actors.Current ?? Actor.System, ct);

    /// <summary>
    /// <paramref name="ct"/> is accepted for the interface's shape only and never reaches the insert (spec U10):
    /// a request whose client disconnected must not lose the security event that disconnection caused. The
    /// insert instead runs on its own 5-second bound, so a genuinely stuck connection cannot hang forever.
    /// </summary>
    public async Task<bool> RecordAsync(SecurityEvent securityEvent, Actor actor, CancellationToken ct)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using var command = dataSource.CreateCommand("""
                insert into platform.audit_log
                  (id, at, category, action, actor_kind, person_id, via, scopes, request_id, request, item_ids, subject_person_id, data, token_id)
                values ($1, $2, 'security', $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
                """);
            command.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
            command.Parameters.Add(new NpgsqlParameter { Value = clock.GetCurrentInstant() });
            command.Parameters.Add(new NpgsqlParameter { Value = securityEvent.Name });
            command.Parameters.Add(new NpgsqlParameter { Value = actor.Kind == ActorKind.Person ? "person" : "system" });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)actor.PersonId?.Value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
            command.Parameters.Add(new NpgsqlParameter { Value = actor.Via.ToWire() });
            command.Parameters.Add(new NpgsqlParameter { Value = actor.Scopes.Order(StringComparer.Ordinal).ToArray() });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)Activity.Current?.TraceId.ToString() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)request.Name ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            command.Parameters.Add(new NpgsqlParameter { Value = securityEvent.Items.Select(i => i.Value).ToArray() });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)securityEvent.Subject?.Value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
            command.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(securityEvent.Data), NpgsqlDbType = NpgsqlDbType.Jsonb });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)actor.TokenId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
            await command.ExecuteNonQueryAsync(bound.Token);
            return true;
        }
        catch (Exception e)
        {
            LogNotRecorded(securityEvent.Name, e);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Security event {Event} could not be recorded")]
    private partial void LogNotRecorded(string @event, Exception exception);
}
