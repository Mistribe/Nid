using Nid.Subscriptions.Contracts;

namespace Nid.Subscriptions.Domain.Catalog;

/// <summary>
/// The merge rules of design §4: a new key is created; a catalog provider is updated and un-archived; an adopted one
/// becomes the catalog one (same key: no subscription changes); a custom one is never overwritten; a catalog provider
/// absent from the reading is archived, never deleted; local labels are never touched. The same commit twice plans
/// nothing: everything comes out unchanged.
/// </summary>
public static class CatalogMerge
{
    public static CatalogPlan Plan(CatalogSnapshot current, CatalogReading reading, string commit)
    {
        var labels = current.Labels.ToDictionary(l => l.Key, StringComparer.Ordinal);
        var labelWrites = new List<Label>();
        List<string> labelsAdded = [], labelsUpdated = [];
        foreach (var label in reading.Labels)
        {
            if (!labels.TryGetValue(label.Key, out var existing))
            {
                labelWrites.Add(new Label(label.Key, label.Name, label.Color, LabelOrigin.Catalog));
                labelsAdded.Add(label.Key);
            }
            else if (existing.Origin == LabelOrigin.Catalog && (existing.Name != label.Name || existing.Color != label.Color))
            {
                labelWrites.Add(existing with { Name = label.Name, Color = label.Color });
                labelsUpdated.Add(label.Key);
            }
        }

        var providers = current.Providers.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var writes = new List<Provider>();
        List<string> added = [], updated = [], unchanged = [], replaced = [], conflicts = [];
        var inCatalog = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in reading.Providers)
        {
            inCatalog.Add(entry.Key);
            var fresh = new Provider(
                entry.Key, ProviderOrigin.Catalog, entry.Name, entry.Website, entry.PricingPage, entry.LabelKeys, commit, null, null, null, entry.IconUrl);
            if (!providers.TryGetValue(entry.Key, out var existing))
            {
                writes.Add(fresh);
                added.Add(entry.Key);
                continue;
            }

            switch (existing.Origin)
            {
                case ProviderOrigin.Custom:
                    conflicts.Add(entry.Key);
                    break;
                case ProviderOrigin.Adopted:
                    writes.Add(fresh);
                    replaced.Add(entry.Key);
                    break;
                default:
                    if (SameContent(existing, entry) && !existing.IsArchived)
                    {
                        unchanged.Add(entry.Key);
                        if (existing.CatalogCommit != commit)
                            writes.Add(fresh); // another commit with the same content: only the commit moves
                    }
                    else
                    {
                        writes.Add(fresh);
                        updated.Add(entry.Key);
                    }
                    break;
            }
        }

        var archive = current.Providers
            .Where(p => p.Origin == ProviderOrigin.Catalog && !p.IsArchived && !inCatalog.Contains(p.Key))
            .Select(p => p.Key).Order(StringComparer.Ordinal).ToList();
        var remaining = current.Providers
            .Where(p => p.Origin == ProviderOrigin.Adopted && !inCatalog.Contains(p.Key))
            .Select(p => p.Key).Order(StringComparer.Ordinal).ToList();

        var report = new CatalogReport(
            Sorted(added), Sorted(updated), Sorted(unchanged), archive, Sorted(replaced), remaining, Sorted(conflicts),
            Sorted(labelsAdded), Sorted(labelsUpdated), reading.LabelsCreated, reading.Duplicates, reading.Refused);
        return new CatalogPlan(labelWrites, writes, archive, report);
    }

    private static bool SameContent(Provider stored, CatalogProvider entry)
        => stored.Name == entry.Name && stored.Website == entry.Website && stored.PricingPage == entry.PricingPage
           && stored.IconUrl == entry.IconUrl
           && stored.LabelKeys.SequenceEqual(entry.LabelKeys, StringComparer.Ordinal);

    private static List<string> Sorted(List<string> keys) => keys.Order(StringComparer.Ordinal).ToList();
}
