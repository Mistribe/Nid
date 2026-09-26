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
    public Task RecordAsync(SecurityEvent securityEvent, CancellationToken ct)
        => RecordAsync(securityEvent, actors.Current ?? Actor.System, ct);

    public async Task RecordAsync(SecurityEvent securityEvent, Actor actor, CancellationToken ct)
    {
        try
        {
            await using var command = dataSource.CreateCommand("""
                insert into platform.audit_log
                  (id, at, category, action, actor_kind, person_id, via, scopes, request_id, request, item_ids, subject_person_id, data)
                values ($1, $2, 'security', $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
                """);
            command.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
            command.Parameters.Add(new NpgsqlParameter { Value = clock.GetCurrentInstant() });
            command.Parameters.Add(new NpgsqlParameter { Value = securityEvent.Name });
            command.Parameters.Add(new NpgsqlParameter { Value = actor.Kind == ActorKind.Person ? "person" : "system" });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)actor.PersonId?.Value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
            command.Parameters.Add(new NpgsqlParameter { Value = actor.Via.ToString().ToLowerInvariant() });
            command.Parameters.Add(new NpgsqlParameter { Value = actor.Scopes.Order(StringComparer.Ordinal).ToArray() });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)Activity.Current?.TraceId.ToString() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)request.Name ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            command.Parameters.Add(new NpgsqlParameter { Value = securityEvent.Items.Select(i => i.Value).ToArray() });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)securityEvent.Subject?.Value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
            command.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(securityEvent.Data), NpgsqlDbType = NpgsqlDbType.Jsonb });
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception e)
        {
            LogNotRecorded(securityEvent.Name, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Security event {Event} could not be recorded")]
    private partial void LogNotRecorded(string @event, Exception exception);
}
