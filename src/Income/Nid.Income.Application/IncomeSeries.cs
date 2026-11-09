using System.Globalization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Income.Domain;
using NodaTime;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Income.Application;

/// <summary>
/// An income source's months (<c>income:&lt;id&gt;</c>, what its page stacks) and the household's (<c>income:household</c>, over the
/// income overview's longest window), each dated the first of the month. A source follows item access, the household total the selector.
/// </summary>
public sealed class IncomeSeries(IVisibleItems visible, IItemRegistry items, IHouseholdSettings household, IInvoker invoker) : ISeriesContribution
{
    private const string Prefix = "income:";
    private const string Household = "income:household";

    public string Module => "income";

    public async Task<IReadOnlyList<SeriesInfo>> ListAsync(PersonId reader, VisibilityScope scope, string? q, IReadOnlyCollection<string> kinds, CancellationToken ct)
    {
        var list = new List<SeriesInfo>();
        var total = await HouseholdInfoAsync(ct);
        if (SeriesSearch.Matches(total, q, kinds))
            list.Add(total);

        var found = await items.SearchAsync(await visible.ForAsync(reader, scope, ct), Module, "source", SeriesSearch.NameFilter(q, kinds, SeriesKinds.Income, "income"), ct);
        list.AddRange(found.Select(i => SourceInfo(i)));
        return list;
    }

    public async Task<SeriesPoints?> PointsAsync(PersonId reader, VisibilityScope scope, string key, LocalDate? from, LocalDate? to, CancellationToken ct)
    {
        if (!key.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        if (key == Household)
        {
            var overview = await invoker.InvokeAsync(new GetIncomeOverviewQuery(scope, OverviewPeriod.ThreeYears, null, null, null, false), ct);
            return overview.TryGetValue(out var all)
                ? new SeriesPoints(await HouseholdInfoAsync(ct), Within(all.Months.Where(m => m.Total is not null).Select(m => Point(m.Year, m.Month, m.Total!.Amount)), from, to))
                : null;
        }

        if (!Guid.TryParse(key[Prefix.Length..], out var guid)) return null;
        var id = new ItemId(guid);
        var source = await invoker.InvokeAsync(new GetIncomeSourceOverviewQuery(id, "all"), ct); // item access, denials journaled
        if (!source.TryGetValue(out var view) || await items.FindAsync(id, ct) is not { } item) return null;
        return new SeriesPoints(SourceInfo(item) with { Unit = $"currency:{view.Currency}" },
            Within(view.Months.Select(m => Point(m.Year, m.Month, m.Total.Amount)), from, to));
    }

    private async Task<SeriesInfo> HouseholdInfoAsync(CancellationToken ct)
        => new(Household, SeriesKinds.IncomeHousehold, null, SeriesUnits.Currency(await household.ReferenceCurrencyAsync(ct)), SeriesFrequencies.Monthly, null, null, null);

    private static SeriesInfo SourceInfo(ItemAccess item)
        => new($"{Prefix}{item.Id.Value}", SeriesKinds.Income, item.DisplayName, $"currency:{item.Currency}", SeriesFrequencies.Monthly, null, null, item.Id);

    private static SeriesPoint Point(int year, int month, string amount)
        => new(new LocalDate(year, month, 1), decimal.Parse(amount, CultureInfo.InvariantCulture));

    private static List<SeriesPoint> Within(IEnumerable<SeriesPoint> points, LocalDate? from, LocalDate? to)
        => points.Where(p => (from is null || p.Date >= from) && (to is null || p.Date <= to)).OrderBy(p => p.Date).ToList();
}
