using Nid.Subscriptions.Contracts;

namespace Nid.Subscriptions.Application.Catalog;

public sealed record LabelView(string Key, string Name, string Color)
{
    public static LabelView From(Label label) => new(label.Key, label.Name, label.Color);
}

/// <summary>What the providers list answers (design §5): key, name, origin, labels, website, archived.</summary>
public sealed record ProviderView(string Key, string Name, string Origin, IReadOnlyList<LabelView> Labels, string? Website, bool Archived)
{
    public static ProviderView From(Provider provider, IReadOnlyDictionary<string, Label> labels)
        => new(provider.Key, provider.Name, provider.Origin.ToWire(), ProviderViews.LabelsOf(provider, labels), provider.Website, provider.IsArchived);
}

/// <summary>The provider of a subscription row or detail (design §5 "Views").</summary>
public sealed record ProviderSummaryView(string Key, string Name, string Origin, IReadOnlyList<LabelView> Labels)
{
    public static ProviderSummaryView From(Provider provider, IReadOnlyDictionary<string, Label> labels)
        => new(provider.Key, provider.Name, provider.Origin.ToWire(), ProviderViews.LabelsOf(provider, labels));
}

public static class ProviderViews
{
    /// <summary>Every label by key: a small reference table, read once per request.</summary>
    public static async Task<IReadOnlyDictionary<string, Label>> LabelsAsync(IProviderStore providers, CancellationToken ct)
        => (await providers.LabelsAsync(ct)).ToDictionary(l => l.Key, StringComparer.Ordinal);

    internal static IReadOnlyList<LabelView> LabelsOf(Provider provider, IReadOnlyDictionary<string, Label> labels)
        => provider.LabelKeys.Where(labels.ContainsKey).Select(key => LabelView.From(labels[key])).ToList();
}
