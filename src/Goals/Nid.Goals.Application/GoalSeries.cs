using System.Globalization;
using Nid.Core.Contracts;
using Nid.Goals.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Goals.Application;

/// <summary>
/// A free goal's readings (<c>goal:&lt;id&gt;</c>, as its page lists them). A goal funded by envelopes, or partly by ones the reader cannot see,
/// has no series: its figure comes from the envelopes. A goal follows item access, not the selector.
/// </summary>
public sealed class GoalSeries(IVisibleItems visible, IItemRegistry items, IGoalStore goals, IHouseholdSettings household, IInvoker invoker) : ISeriesContribution
{
    private const string Prefix = "goal:";

    public string Module => "goals";

    public async Task<IReadOnlyList<SeriesInfo>> ListAsync(PersonId reader, VisibilityScope scope, string? q, IReadOnlyCollection<string> kinds, CancellationToken ct)
    {
        var ids = await visible.ForAsync(reader, scope, ct);
        var filter = SeriesSearch.NameFilter(q, kinds, SeriesKinds.Goal, "goal");
        var found = new List<ItemAccess>();
        foreach (var kind in GoalKinds.All)
            found.AddRange(await items.SearchAsync(ids, Module, kind, filter, ct));
        if (found.Count == 0) return [];

        var funded = (await goals.FundingOfAsync(found.Select(g => g.Id).ToList(), ct)).Select(f => f.Goal).ToHashSet();
        var unit = SeriesUnits.Currency(await household.ReferenceCurrencyAsync(ct));
        return found.Where(g => !funded.Contains(g.Id)).Select(g => InfoOf(g, unit)).ToList();
    }

    public async Task<SeriesPoints?> PointsAsync(PersonId reader, VisibilityScope scope, string key, LocalDate? from, LocalDate? to, CancellationToken ct)
    {
        if (!key.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParse(key[Prefix.Length..], out var guid)) return null;
        var id = new ItemId(guid);
        var read = await invoker.InvokeAsync(new GetGoalQuery(id, scope), ct); // item access, denials journaled; null for a non-goal
        if (!read.TryGetValue(out var detail) || detail.Funding.Count > 0 || detail.Goal.Partial || await items.FindAsync(id, ct) is not { } item) return null;
        var unit = SeriesUnits.Currency(await household.ReferenceCurrencyAsync(ct));
        return new SeriesPoints(InfoOf(item, unit),
            SeriesWindow.Within(detail.Entries.Select(e => new SeriesPoint(e.On, decimal.Parse(e.Amount.Amount, CultureInfo.InvariantCulture))), from, to));
    }

    private static SeriesInfo InfoOf(ItemAccess goal, string unit)
        => new($"{Prefix}{goal.Id.Value}", SeriesKinds.Goal, goal.DisplayName, unit, SeriesFrequencies.Irregular, null, null, goal.Id);
}
