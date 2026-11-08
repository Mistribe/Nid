using Nid.Subscriptions.Contracts;

namespace Nid.Subscriptions.Application.Catalog;

public sealed record LabelView(string Key, string Name, string Color)
{
    public static LabelView From(Label label) => new(label.Key, label.Name, label.Color);
}

/// <summary>
/// What the providers list answers (design §5): key, name, origin, labels, website, archived — and, from 1C2c, the
/// relative URL of its icon on Nid's origin, or <c>null</c> (the web then shows a monogram) — and the providers a
/// bundle includes.
/// </summary>
public sealed record ProviderView(
    string Key, string Name, string Origin, IReadOnlyList<LabelView> Labels, string? Website, bool Archived, string? IconUrl,
    IReadOnlyList<IncludedProviderView> Included)
{
    public static ProviderView From(Provider provider, IReadOnlyDictionary<string, Label> labels, IReadOnlyDictionary<string, Provider> included)
        => new(provider.Key, provider.Name, provider.Origin.ToWire(), ProviderViews.LabelsOf(provider, labels), provider.Website,
            provider.IsArchived, ProviderViews.IconUrlOf(provider), ProviderViews.IncludedOf(provider, labels, included));
}

/// <summary>A provider included in a bundle: what shows it. The link goes from the bundle to its services only.</summary>
public sealed record IncludedProviderView(string Key, string Name, IReadOnlyList<LabelView> Labels, string? IconUrl);

/// <summary>The provider of a subscription row or detail (design §5 "Views"), with its icon URL (1C2c design §7).</summary>
public sealed record ProviderSummaryView(
    string Key, string Name, string Origin, IReadOnlyList<LabelView> Labels, string? IconUrl, IReadOnlyList<IncludedProviderView> Included)
{
    public static ProviderSummaryView From(Provider provider, IReadOnlyDictionary<string, Label> labels, IReadOnlyDictionary<string, Provider> included)
        => new(provider.Key, provider.Name, provider.Origin.ToWire(), ProviderViews.LabelsOf(provider, labels), ProviderViews.IconUrlOf(provider),
            ProviderViews.IncludedOf(provider, labels, included));
}

public static class ProviderViews
{
    /// <summary>Every label by key: a small reference table, read once per request.</summary>
    public static async Task<IReadOnlyDictionary<string, Label>> LabelsAsync(IProviderStore providers, CancellationToken ct)
        => (await providers.LabelsAsync(ct)).ToDictionary(l => l.Key, StringComparer.Ordinal);

    /// <summary>For a provider that includes nothing.</summary>
    internal static IReadOnlyDictionary<string, Provider> NoIncluded { get; } = new Dictionary<string, Provider>(StringComparer.Ordinal);

    /// <summary>What <paramref name="bundles"/> include, by key; no query when they include nothing.</summary>
    internal static async Task<IReadOnlyDictionary<string, Provider>> IncludedAsync(
        IProviderStore providers, IEnumerable<Provider> bundles, CancellationToken ct)
    {
        var keys = bundles.SelectMany(p => p.IncludedKeys).Distinct(StringComparer.Ordinal).ToList();
        return keys.Count == 0 ? NoIncluded : await providers.FindAsync(keys, ct);
    }

    /// <summary>
    /// Catalog providers only, public to everyone: a catalog key can also be a custom provider's (a conflict, never
    /// overwritten), which this reader may not see. An archived one stays: the bundle's stored list still names it.
    /// </summary>
    internal static IReadOnlyList<IncludedProviderView> IncludedOf(
        Provider provider, IReadOnlyDictionary<string, Label> labels, IReadOnlyDictionary<string, Provider> included)
        => provider.IncludedKeys
            .Where(key => included.TryGetValue(key, out var found) && found.Origin == ProviderOrigin.Catalog)
            .Select(key => included[key])
            .Select(p => new IncludedProviderView(p.Key, p.Name, LabelsOf(p, labels), IconUrlOf(p)))
            .ToList();

    internal static IReadOnlyList<LabelView> LabelsOf(Provider provider, IReadOnlyDictionary<string, Label> labels)
        => provider.LabelKeys.Where(labels.ContainsKey).Select(key => LabelView.From(labels[key])).ToList();

    /// <summary>
    /// 1C2c design §7: the route of the stored icon, which only a catalog provider has (the route refuses any other).
    /// A declared icon not stored yet, or refused, has none. The server writes the URL; the web never builds one.
    /// </summary>
    public static string? IconUrlOf(Provider provider)
        => provider.Origin == ProviderOrigin.Catalog && provider.IconAsset is { } hash ? IconLimits.PathPrefix + hash : null;
}
