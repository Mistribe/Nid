using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;

namespace Nid.Subscriptions.Application.Catalog;

/// <summary>
/// Spec §3.6 (amended after 1G, and after the Task 5 review: "Judged as of the sync, presented as if never adopted").
/// The entries naming an adopted or a custom provider (adopted replaced, adopted remaining, custom conflicts) come
/// from the household's subscriptions, private ones included. Each is kept only if the reader can see, now, one of the
/// subscriptions that used the key when the sync ran (<see cref="CatalogSyncRun.UsedBy"/>): linking a subscription
/// afterwards changes nothing. A replaced key the reader cannot see is presented as added — what a household that
/// never used it would have seen — so the public catalog's keys stay accounted for and subtracting them reveals
/// nothing. A hidden remaining or conflict entry is dropped, with no count left behind. The catalog's own entries
/// pass as they are. Only the system actor reads the report unfiltered.
/// </summary>
public sealed class CatalogReportFilter
{
    private static readonly IReadOnlySet<ItemId> Nothing = new HashSet<ItemId>();

    private readonly IReadOnlySet<ItemId>? visible;

    private CatalogReportFilter(IReadOnlySet<ItemId>? visible) => this.visible = visible;

    /// <summary>The system actor (the job) is the host: it gets the complete report.</summary>
    public static CatalogReportFilter None { get; } = new(null);

    /// <summary>
    /// The filter for <paramref name="reader"/>: none for <see cref="ActorKind.System"/> only, whatever a person's
    /// role; one visibility query for every run read — always, even when no run holds a household entry, so the time
    /// the journal takes says nothing about whether one exists (final review H1).
    /// </summary>
    public static async Task<CatalogReportFilter> ForAsync(
        Actor? reader, IVisibleItems visible, CancellationToken ct)
    {
        if (reader?.Kind == ActorKind.System)
            return None;
        if (reader?.PersonId is not { } person)
            return new CatalogReportFilter(Nothing);

        return new CatalogReportFilter(await visible.ForAsync(person, VisibilityScope.Me, ct));
    }

    /// <summary>The run's report as this reader may read it; null while the run has none.</summary>
    public CatalogReport? Apply(CatalogSyncRun run)
    {
        if (run.Report is not { } report || visible is null)
            return run.Report;

        bool Seen(string key) => run.UsedBy.TryGetValue(key, out var items) && items.Any(visible.Contains);

        var hiddenReplaced = report.AdoptedReplaced.Where(k => !Seen(k)).ToList();
        return report with
        {
            ProvidersAdded = report.ProvidersAdded.Concat(hiddenReplaced).Order(StringComparer.Ordinal).ToList(),
            AdoptedReplaced = report.AdoptedReplaced.Where(Seen).ToList(),
            AdoptedRemaining = report.AdoptedRemaining.Where(Seen).ToList(),
            Conflicts = report.Conflicts.Where(Seen).ToList(),
        };
    }
}
