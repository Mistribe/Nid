using Nid.Core.Contracts;

namespace Nid.Platform.Contracts;

public sealed record KeyedRow(Guid Id, string? Ref, PersonId? ImportedBy);

/// <summary>
/// 1D2 design §5.1 (moved from Subscriptions for every module's export): a row's file id is its <c>import_ref</c> when no other exported row of its section has the same
/// one; on a collision the reader's own row keeps it. Every other row gets <c>&lt;prefix&gt;-&lt;uuid&gt;</c>, and a
/// ref that equals some row's derived key gives way to it, so ids are always unique.
/// </summary>
public static class FileIds
{
    public static IReadOnlyDictionary<Guid, string> Assign(string prefix, IReadOnlyList<KeyedRow> rows, PersonId reader)
    {
        string Derived(Guid id) => $"{prefix}-{id:D}";
        var derived = rows.Select(r => Derived(r.Id)).ToHashSet(StringComparer.Ordinal);
        var ids = new Dictionary<Guid, string>();
        foreach (var group in rows.Where(r => r.Ref is not null).GroupBy(r => r.Ref!, StringComparer.Ordinal))
        {
            var keeper = group.Count() == 1
                ? group.Single()
                : group.Count(r => r.ImportedBy == reader) == 1 ? group.Single(r => r.ImportedBy == reader) : null;
            if (keeper is not null && !derived.Contains(group.Key))
                ids[keeper.Id] = group.Key;
        }
        foreach (var row in rows.Where(r => !ids.ContainsKey(r.Id)))
            ids[row.Id] = Derived(row.Id);
        return ids;
    }
}

/// <summary>A stored row imported under a file id: its id, hash, importer, and the items that decide who sees it.</summary>
public sealed record ImportCandidate(Guid Id, string Hash, PersonId? ImportedBy, IReadOnlyList<ItemId> Items);

/// <summary>What a file id resolves to: a row the importer sees, their own row they no longer see, or nothing.</summary>
public sealed record ImportResolution(ImportCandidate? Row, bool OwnHidden)
{
    public static ImportResolution New { get; } = new(null, false);
}

/// <summary>
/// Import provenance privacy (spec 2026-09-25 §4), shared by every section. A candidate is visible when the importer
/// can read one of its items. The importer's own visible row wins, then the visible row with the smallest id. With
/// none visible, the importer's own row is <see cref="ImportResolution.OwnHidden"/>. Another adult's row the importer
/// cannot see gives the answer an unused id gets. <paramref name="belongs"/> narrows the rows of others only: the
/// importer's own rows are always candidates, since the unique index (imported_by, import_ref) forbids a second one.
/// </summary>
public static class ImportResolver
{
    public static async Task<ImportResolution> ResolveAsync(
        PersonId actor, IReadOnlyList<ImportCandidate>? candidates, Func<ItemId, Task<bool>> canRead, Func<ImportCandidate, bool>? belongs = null)
    {
        if (candidates is null || candidates.Count == 0)
            return ImportResolution.New;

        foreach (var candidate in candidates
                     .Where(c => c.ImportedBy == actor || belongs is null || belongs(c))
                     .OrderBy(c => c.ImportedBy == actor ? 0 : 1).ThenBy(c => c.Id))
            foreach (var item in candidate.Items)
                if (await canRead(item))
                    return new ImportResolution(candidate, false);

        return candidates.Any(c => c.ImportedBy == actor) ? new ImportResolution(null, true) : ImportResolution.New;
    }
}
