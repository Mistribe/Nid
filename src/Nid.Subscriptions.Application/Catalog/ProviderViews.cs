using Nid.Subscriptions.Contracts;

namespace Nid.Subscriptions.Application.Catalog;

public sealed record LabelView(string Key, string Name, string Color)
{
    public static LabelView From(Label label) => new(label.Key, label.Name, label.Color);
}

/// <summary>
/// What the providers list answers (design §5): key, name, origin, labels, website, archived — and, from 1C2c, the
/// relative URL of its icon on Nid's origin, or <c>null</c> (the web then shows a monogram).
/// </summary>
public sealed record ProviderView(
    string Key, string Name, string Origin, IReadOnlyList<LabelView> Labels, string? Website, bool Archived, string? IconUrl)
{
    public static ProviderView From(Provider provider, IReadOnlyDictionary<string, Label> labels)
        => new(provider.Key, provider.Name, provider.Origin.ToWire(), ProviderViews.LabelsOf(provider, labels), provider.Website,
            provider.IsArchived, ProviderViews.IconUrlOf(provider));
}

/// <summary>The provider of a subscription row or detail (design §5 "Views"), with its icon URL (1C2c design §7).</summary>
public sealed record ProviderSummaryView(string Key, string Name, string Origin, IReadOnlyList<LabelView> Labels, string? IconUrl)
{
    public static ProviderSummaryView From(Provider provider, IReadOnlyDictionary<string, Label> labels)
        => new(provider.Key, provider.Name, provider.Origin.ToWire(), ProviderViews.LabelsOf(provider, labels), ProviderViews.IconUrlOf(provider));
}

public static class ProviderViews
{
    /// <summary>Every label by key: a small reference table, read once per request.</summary>
    public static async Task<IReadOnlyDictionary<string, Label>> LabelsAsync(IProviderStore providers, CancellationToken ct)
        => (await providers.LabelsAsync(ct)).ToDictionary(l => l.Key, StringComparer.Ordinal);

    internal static IReadOnlyList<LabelView> LabelsOf(Provider provider, IReadOnlyDictionary<string, Label> labels)
        => provider.LabelKeys.Where(labels.ContainsKey).Select(key => LabelView.From(labels[key])).ToList();

    /// <summary>
    /// 1C2c design §7: the route of the stored icon, which only a catalog provider has (the route refuses any other).
    /// A declared icon not stored yet, or refused, has none. The server writes the URL; the web never builds one.
    /// </summary>
    public static string? IconUrlOf(Provider provider)
        => provider.Origin == ProviderOrigin.Catalog && provider.IconAsset is { } hash ? IconLimits.PathPrefix + hash : null;
}
