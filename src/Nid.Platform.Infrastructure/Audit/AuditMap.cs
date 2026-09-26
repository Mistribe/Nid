using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Nid.Platform.Infrastructure.Audit;

/// <summary>How to find the items a row belongs to (spec §4.4): what an item's history, filtered by Readers, is keyed on.</summary>
public sealed record AuditRule(Func<EntityEntry, IEnumerable<Guid>> Items)
{
    /// <summary>A row that belongs to no item: audited, never shown in an item's history.</summary>
    public static AuditRule NoItem { get; } = new(_ => []);
}

/// <summary>Each module says, for every row type it maps, whether it is audited and under which items, or excluded.</summary>
public interface IAuditMap
{
    IReadOnlyDictionary<Type, AuditRule> Audited { get; }

    IReadOnlySet<Type> Excluded { get; }
}

public static class AuditItems
{
    /// <summary>The original and current values of a Guid property, so a row moved between items is under both.</summary>
    public static IEnumerable<Guid> Of(EntityEntry entry, string property)
    {
        var value = entry.Property(property);
        return new[] { value.OriginalValue, value.CurrentValue }.OfType<Guid>().Distinct();
    }
}

/// <summary>An audited row changed while no actor was set: the write is refused rather than left unaudited (spec U4).</summary>
public sealed class AuditActorMissingException(string entity)
    : InvalidOperationException($"{entity} was written with no actor: every audited write needs one (1F3a spec U4).");
