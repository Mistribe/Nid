using System.Text;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;

namespace Nid.Subscriptions.Application.Import;

public sealed record KeyedRow(Guid Id, ImportRef? Import);

/// <summary>
/// 1D2 design §5.1: a row's file id is its <c>import_ref</c> when no other exported row of its section has the same
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
        foreach (var group in rows.Where(r => r.Import is not null).GroupBy(r => r.Import!.Ref, StringComparer.Ordinal))
        {
            var keeper = group.Count() == 1
                ? group.Single()
                : group.Count(r => r.Import!.ImportedBy == reader) == 1 ? group.Single(r => r.Import!.ImportedBy == reader) : null;
            if (keeper is not null && !derived.Contains(group.Key))
                ids[keeper.Id] = group.Key;
        }
        foreach (var row in rows.Where(r => !ids.ContainsKey(r.Id)))
            ids[row.Id] = Derived(row.Id);
        return ids;
    }
}

public sealed record KeyedProvider(string StoredKey, string Name, ImportRef? Import, PersonId? CreatedBy);

/// <summary>
/// 1D2 design §5.2: the import ref first (it survives a round trip, the generated stored key does not), then the stored
/// key, then a slug of an adopted key; a key already used by the file — another provider's, or a catalog key that a
/// subscription names directly — takes a <c>-2</c>, <c>-3</c>… suffix.
/// </summary>
public static class ProviderFileKeys
{
    public static IReadOnlyDictionary<string, string> Assign(IReadOnlyList<KeyedProvider> providers, IReadOnlySet<string> taken, PersonId reader)
    {
        var refOwners = providers.Where(p => p.Import is not null).GroupBy(p => p.Import!.Ref, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count() == 1 ? g.Single() : g.Count(p => p.CreatedBy == reader) == 1 ? g.Single(p => p.CreatedBy == reader) : null,
                StringComparer.Ordinal);

        var used = new HashSet<string>(taken, StringComparer.Ordinal);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in providers.OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.StoredKey, StringComparer.Ordinal))
        {
            var wanted = provider.Import is { } import && refOwners[import.Ref] == provider
                ? import.Ref
                : NidDataJson.IsFileId(provider.StoredKey) ? provider.StoredKey : Slug(provider.StoredKey);
            var key = wanted;
            for (var n = 2; !used.Add(key); n++)
                key = $"{wanted}-{n}";
            keys[provider.StoredKey] = key;
        }
        return keys;
    }

    private static string Slug(string key)
    {
        var slug = new StringBuilder();
        foreach (var c in key.ToLowerInvariant())
            slug.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');
        var text = string.Join('-', slug.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? "provider" : text;
    }
}
