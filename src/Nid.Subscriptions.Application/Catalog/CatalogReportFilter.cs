using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;

namespace Nid.Subscriptions.Application.Catalog;

/// <summary>
/// Spec §3.6 (amended after 1G): a sync report, filtered for its reader. The entries naming an adopted or a custom
/// provider (adopted replaced, adopted remaining, custom conflicts) come from the household's subscriptions, private
/// ones included; each is kept only if the reader could see it by §3.5 (a subscription visible to them uses the key,
/// or they created the custom provider). A replaced adopted provider is a catalog one by now: it is judged by the
/// subscriptions that use it, as while it was adopted, or the catalog rule would show every key the household used.
/// Dropped entries leave no count behind. The catalog's own entries pass as they are.
/// </summary>
public sealed class CatalogReportFilter
{
    private readonly IReadOnlySet<string>? seen;

    private CatalogReportFilter(IReadOnlySet<string>? seen) => this.seen = seen;

    /// <summary>The system actor (the job, no person) is the host: it gets the complete report.</summary>
    public static CatalogReportFilter None { get; } = new(null);

    /// <summary>One subscription query for every run read, whatever their number.</summary>
    public static async Task<CatalogReportFilter> ForAsync(
        PersonId? reader, IReadOnlyList<CatalogSyncRun> runs, IVisibleItems visible, IProviderStore providers, CancellationToken ct)
    {
        if (reader is not { } person)
            return None;

        var keys = runs.Where(r => r.Report is not null).SelectMany(r => Household(r.Report!)).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0)
            return new CatalogReportFilter(new HashSet<string>(StringComparer.Ordinal));

        var items = await visible.ForAsync(person, VisibilityScope.Me, ct);
        return new CatalogReportFilter(await providers.SeenByAsync(person, items, keys, ct));
    }

    public CatalogReport Apply(CatalogReport report)
        => seen is null
            ? report
            : report with
            {
                AdoptedReplaced = Keep(report.AdoptedReplaced),
                AdoptedRemaining = Keep(report.AdoptedRemaining),
                Conflicts = Keep(report.Conflicts),
            };

    private IReadOnlyList<string> Keep(IReadOnlyList<string> keys) => keys.Where(seen!.Contains).ToList();

    private static IEnumerable<string> Household(CatalogReport report)
        => report.AdoptedReplaced.Concat(report.AdoptedRemaining).Concat(report.Conflicts);
}
