using System.Globalization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using NodaTime;

namespace Nid.RealEstate.Application;

/// <summary>
/// A property's estimates (<c>property:&lt;id&gt;:value</c>), cost price (<c>:cost</c>) and net value (<c>:net</c>), as the valuation and the
/// overview screens draw them. They follow item access, not the selector.
/// </summary>
public sealed class PropertySeries(
    IVisibleItems visible, IItemRegistry items, IAccessPolicy policy, IAccessDenials denials, IPropertyStore properties, ILoanStore loans,
    IValuations valuations, IFlows flows, IHouseholdCalendar calendar, PropertiesOverviewReader overview) : ISeriesContribution
{
    private const string Value = "value";
    private const string Cost = "cost";
    private const string Net = "net";

    public string Module => "realestate";

    public async Task<IReadOnlyList<SeriesInfo>> ListAsync(PersonId reader, VisibilityScope scope, string? q, IReadOnlyCollection<string> kinds, CancellationToken ct)
    {
        var ids = await visible.ForAsync(reader, scope, ct);
        var list = new List<SeriesInfo>();
        foreach (var (kind, suffix) in new[] { (SeriesKinds.PropertyValue, Value), (SeriesKinds.PropertyCost, Cost), (SeriesKinds.PropertyNet, Net) })
            list.AddRange((await items.SearchAsync(ids, Module, "property", SeriesSearch.NameFilter(q, kinds, kind, "property", suffix), ct)).Select(p => Info(p, suffix)));
        return list;
    }

    public async Task<SeriesPoints?> PointsAsync(PersonId reader, VisibilityScope scope, string key, LocalDate? from, LocalDate? to, CancellationToken ct)
    {
        if (key.Split(':') is not ["property", var raw, (Value or Cost or Net) and var suffix] || !Guid.TryParse(raw, out var guid)) return null;
        var id = new ItemId(guid);
        if ((await policy.CheckAsync(reader, id, AccessKind.Read, ct)).TryGetFailure(out _))
        {
            await denials.RecordAsync(reader, id, "read", ct); // as the item-scoped pipeline does
            return null;
        }

        if (await items.FindAsync(id, ct) is not { } item) return null;
        var today = await calendar.TodayAsync(ct);
        var currency = LoanViews.CurrencyOf(item);
        IEnumerable<SeriesPoint> points;
        if (suffix == Net)
        {
            if (await properties.FindAsync(id, ct) is null) return null;
            var mine = await visible.ForAsync(reader, VisibilityScope.Me, ct); // the loans the overview counts
            var linked = (await loans.FindLinkedAsync([id], ct)).Select(l => l.ItemId).Where(mine.Contains);
            var read = await overview.ReadAsync(reader, new HashSet<ItemId>(linked) { id }, currency, PropertiesSpan.All, today, ct);
            if (!read.TryGetValue(out var view)) return null;
            points = view.Points.Where(p => p.NetValue is not null).Select(p => new SeriesPoint(p.On, Parse(p.NetValue!.Amount)));
        }
        else
        {
            var read = await OverviewProperties.ReadAsync(new HashSet<ItemId> { id }, reader, properties, items, loans, valuations, flows, policy, ct);
            if (!read.TryGetValue(out var set) || set.Count == 0) return null;
            var row = PropertiesValuationMath.Build(set, currency, RateTable.Empty(today, 0), today).Properties[0];
            points = suffix == Value
                ? row.Estimates.Select(e => new SeriesPoint(e.On, Parse(e.Value)))
                : row.CostSteps.Select(c => new SeriesPoint(c.On, Parse(c.CostPrice)));
        }

        return new SeriesPoints(Info(item, suffix), SeriesWindow.Within(points, from, to));
    }

    private static decimal Parse(string amount) => decimal.Parse(amount, CultureInfo.InvariantCulture);

    private static SeriesInfo Info(ItemAccess property, string suffix)
        => new($"property:{property.Id.Value}:{suffix}", suffix switch { Value => SeriesKinds.PropertyValue, Cost => SeriesKinds.PropertyCost, _ => SeriesKinds.PropertyNet },
            property.DisplayName, $"currency:{property.Currency}", suffix == Net ? SeriesFrequencies.Monthly : SeriesFrequencies.Irregular, null, null, property.Id);
}
