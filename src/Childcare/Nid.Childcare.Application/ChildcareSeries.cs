using System.Globalization;
using Nid.Childcare.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Childcare.Application;

/// <summary>A childcare arrangement's validated months (<c>childcare:&lt;id&gt;:monthly</c>), dated the first of the month; a month not validated has no cost, so no point. It follows item access.</summary>
public sealed class ChildcareSeries(IVisibleItems visible, IItemRegistry items, IHouseholdSettings household, IInvoker invoker) : ISeriesContribution
{
    public string Module => ChildcareModule.Key;

    public async Task<IReadOnlyList<SeriesInfo>> ListAsync(PersonId reader, VisibilityScope scope, string? q, IReadOnlyCollection<string> kinds, CancellationToken ct)
    {
        var found = await items.SearchAsync(await visible.ForAsync(reader, scope, ct), Module, ChildcareKinds.Childminder,
            SeriesSearch.NameFilter(q, kinds, SeriesKinds.ChildcareMonthly, "childcare", "monthly"), ct);
        if (found.Count == 0) return [];
        var unit = SeriesUnits.Currency(await household.ReferenceCurrencyAsync(ct));
        return found.Select(a => InfoOf(a, unit)).ToList();
    }

    public async Task<SeriesPoints?> PointsAsync(PersonId reader, VisibilityScope scope, string key, LocalDate? from, LocalDate? to, CancellationToken ct)
    {
        if (key.Split(':') is not ["childcare", var raw, "monthly"] || !Guid.TryParse(raw, out var guid)) return null;
        var id = new ItemId(guid);
        var read = await invoker.InvokeAsync(new ListStatementsQuery(id), ct); // item access, denials journaled; null for a non-arrangement
        if (!read.TryGetValue(out var list) || await items.FindAsync(id, ct) is not { } item) return null;
        var unit = SeriesUnits.Currency(await household.ReferenceCurrencyAsync(ct));
        return new SeriesPoints(InfoOf(item, unit),
            SeriesWindow.Within(list.Months.Where(m => m.Cost is not null)
                .Select(m => new SeriesPoint(FirstOf(m.Month), decimal.Parse(m.Cost!.Amount, CultureInfo.InvariantCulture))), from, to));
    }

    private static LocalDate FirstOf(string month) => new(int.Parse(month[..4], CultureInfo.InvariantCulture), int.Parse(month[5..7], CultureInfo.InvariantCulture), 1);

    private static SeriesInfo InfoOf(ItemAccess arrangement, string unit)
        => new($"childcare:{arrangement.Id.Value}:monthly", SeriesKinds.ChildcareMonthly, arrangement.DisplayName, unit, SeriesFrequencies.Monthly, null, null, arrangement.Id);
}
