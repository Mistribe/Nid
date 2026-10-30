using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;

namespace Nid.Platform.Infrastructure.Audit;

/// <summary>Turns one change-tracker entry into its log line (spec §4.3).</summary>
internal static class AuditDiff
{
    public static AuditEntryRow? Row(EntityEntry entry, Actor actor, string? request, IEnumerable<Guid> items, Instant at, IReadOnlySet<string>? secret)
    {
        var columns = entry.Properties
            .Where(p => p.Metadata.GetColumnName() is var column && column != "xmin" && secret?.Contains(column) != true).ToList();
        JsonObject? before = null, after = null;
        string action;
        switch (entry.State)
        {
            case EntityState.Added:
                action = "insert";
                after = Values(columns.Where(p => !IsUnsetStoreValue(p)), p => p.CurrentValue);
                break;
            case EntityState.Deleted:
                action = "delete";
                before = Values(columns, p => p.OriginalValue);
                break;
            default:
                action = "update";
                var changed = columns.Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)).ToList();
                if (changed.Count == 0)
                    return null; // only the concurrency token moved: nothing to record
                before = Values(changed, p => p.OriginalValue);
                after = Values(changed, p => p.CurrentValue);
                break;
        }

        return new AuditEntryRow
        {
            Id = Guid.CreateVersion7(),
            At = at,
            Category = "write",
            Action = action,
            ActorKind = actor.Kind == ActorKind.Person ? "person" : "system",
            PersonId = actor.PersonId?.Value,
            Via = actor.Via.ToWire(),
            Scopes = actor.Scopes.Order(StringComparer.Ordinal).ToArray(),
            TokenId = actor.TokenId,
            RequestId = Activity.Current?.TraceId.ToString(),
            Request = request,
            Entity = $"{entry.Metadata.GetSchema()}.{entry.Metadata.GetTableName()}",
            EntityKey = Key(entry),
            ItemIds = items.Distinct().ToArray(),
            Data = new JsonObject { ["before"] = before, ["after"] = after }.ToJsonString(),
        };
    }

    /// <summary>A value the database fills in (a <c>default now()</c>) that the application left unset.</summary>
    private static bool IsUnsetStoreValue(PropertyEntry property)
        => property.Metadata.ValueGenerated != ValueGenerated.Never
            && Equals(property.CurrentValue, property.Metadata.ClrType.IsValueType ? Activator.CreateInstance(property.Metadata.ClrType) : null);

    private static JsonObject Values(IEnumerable<PropertyEntry> properties, Func<PropertyEntry, object?> read)
    {
        var values = new JsonObject();
        foreach (var property in properties)
            values[property.Metadata.GetColumnName()] = Json(read(property));
        return values;
    }

    private static string Key(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey()!.Properties;
        if (key.Count == 1)
            return Json(entry.Property(key[0].Name).CurrentValue)?.ToString() ?? "";
        return Values(key.Select(k => entry.Property(k.Name)), p => p.CurrentValue).ToJsonString();
    }

    // The wire format (architecture.md §7): amounts as decimal strings, dates and instants in ISO 8601.
    private static JsonNode? Json(object? value) => value switch
    {
        null => null,
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        LocalDate date => LocalDatePattern.Iso.Format(date),
        Instant instant => InstantPattern.ExtendedIso.Format(instant),
        Guid g => g.ToString(),
        string s => s,
        bool b => b,
        int i => i,
        long l => l,
        IEnumerable<string> list => new JsonArray(list.Select(x => (JsonNode?)x).ToArray()),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };
}
