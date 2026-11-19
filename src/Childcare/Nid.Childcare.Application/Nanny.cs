using System.Globalization;
using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Childcare.Application;

/// <summary>#94: a month of a nanny at home, as paid: <c>Paid</c> the net salary and the employer contributions, <c>Cmg</c> the aid received.</summary>
public sealed record NannyMonth(ItemId ItemId, YearMonth Month, decimal Paid, decimal Cmg, decimal? Hours = null)
{
    public decimal NetCost => Paid - Cmg;
}

/// <summary>#94: a nanny item (kind <see cref="ChildcareKinds.Nanny"/>) and its months; the item itself is the registry's.</summary>
public interface INannyStore
{
    Task CreateAsync(ItemId id, CancellationToken ct, ImportMark? import = null);

    /// <summary>The nannies among <paramref name="ids"/>.</summary>
    Task<IReadOnlyList<ItemId>> FindManyAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct);

    /// <summary>The months of these nannies, by item then month.</summary>
    Task<IReadOnlyList<NannyMonth>> MonthsOfAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct);

    /// <summary>Records the month, or replaces the one stored.</summary>
    Task PutMonthAsync(NannyMonth month, CancellationToken ct);

    /// <summary>False when the month was not recorded.</summary>
    Task<bool> DeleteMonthAsync(ItemId id, YearMonth month, CancellationToken ct);

    Task<IReadOnlyDictionary<string, IReadOnlyList<ImportedArrangement>>> FindImportedAsync(IReadOnlyCollection<string> refs, CancellationToken ct);

    Task<IReadOnlyDictionary<ItemId, ImportMark>> ImportMarksAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct);

    /// <summary>An import replacing a nanny: its months, every one, and its mark.</summary>
    Task ReplaceImportedAsync(ItemId id, IReadOnlyList<NannyMonth> months, ImportMark import, CancellationToken ct);
}

/// <summary>#94: a nanny's year from its recorded months. Pure.</summary>
public static class NannyYear
{
    public sealed record Totals(decimal Paid, decimal Cmg, decimal NetCost, int Months);

    public static IReadOnlyList<NannyMonth> Of(IEnumerable<NannyMonth> months, int year)
        => months.Where(m => m.Month.Year == year).OrderBy(m => m.Month).ToList();

    public static Totals Sum(IReadOnlyList<NannyMonth> recorded)
        => new(recorded.Sum(m => m.Paid), recorded.Sum(m => m.Cmg), recorded.Sum(m => m.NetCost), recorded.Count);

    /// <summary>
    /// The current year's months after the latest recorded one, up to December, each at the mean net cost of the year's recorded months
    /// (as <see cref="ChildcareYear.Projected"/> does for a childminder). None for another year, without a recorded month, or once closed.
    /// </summary>
    public static IReadOnlyList<(YearMonth Month, decimal Amount)> Projected(IEnumerable<NannyMonth> months, int year, LocalDate today, bool closed)
    {
        var recorded = Of(months, year);
        if (year != today.Year || closed || recorded.Count == 0)
            return [];
        var mean = Mensualisation.Round(recorded.Average(m => m.NetCost));
        return Enumerable.Range(recorded[^1].Month.Month + 1, 12 - recorded[^1].Month.Month).Select(m => (new YearMonth(year, m), mean)).ToList();
    }

    /// <summary>The years with a recorded month and the current one, newest first.</summary>
    public static IReadOnlyList<int> Years(IEnumerable<NannyMonth> months, LocalDate today)
        => months.Select(m => m.Month.Year).Append(today.Year).Distinct().OrderDescending().ToList();
}

/// <summary>#94: a nanny in a list. <c>NetCost</c>, <c>Months</c>: the current year's.</summary>
public sealed record NannySummary(
    Guid Id, string Name, IReadOnlyList<ItemOwnerView> Owners, string Visibility, bool Closed, bool Writable, ChildcareMoneyView NetCost, int Months);

public sealed record NannyList(IReadOnlyList<NannySummary> Items);

/// <summary>A recorded month; <c>NetCost</c> is <c>Paid</c> − <c>Cmg</c>. <c>Hours</c>: as entered, null when not.</summary>
public sealed record NannyMonthView(string Month, ChildcareMoneyView Paid, ChildcareMoneyView Cmg, ChildcareMoneyView NetCost, string? Hours);

/// <summary>The current year's months left, counted at <c>Mean</c>; <c>Total</c>: the recorded net cost plus them, what the tax estimate counts.</summary>
public sealed record NannyProjectionView(int Months, ChildcareMoneyView Mean, ChildcareMoneyView Total);

/// <summary>#94: a nanny and its months of <c>Year</c>, newest first, with the year's totals.</summary>
public sealed record NannyDetail(
    NannySummary Summary, int Year, IReadOnlyList<int> Years, IReadOnlyList<NannyMonthView> Months,
    ChildcareMoneyView Paid, ChildcareMoneyView Cmg, ChildcareMoneyView NetCost, NannyProjectionView? Projection);

internal static class NannyViews
{
    public static string Format(YearMonth month) => month.ToString("uuuu-MM", CultureInfo.InvariantCulture);

    public static NannySummary Summary(ItemAccess item, bool writable, IReadOnlyList<NannyMonth> months, LocalDate today, Currency currency)
    {
        var year = NannyYear.Sum(NannyYear.Of(months, today.Year));
        return new NannySummary(item.Id.Value, item.DisplayName, item.OwnerViews(), item.Visibility == Visibility.Private ? "private" : "shared",
            item.ArchivedAt is not null, writable, ChildcareMoneyView.Of(year.NetCost, currency), year.Months);
    }

    public static NannyDetail Detail(NannySummary summary, int year, IReadOnlyList<NannyMonth> months, LocalDate today, Currency currency)
    {
        ChildcareMoneyView Money(decimal amount) => ChildcareMoneyView.Of(amount, currency);
        var recorded = NannyYear.Of(months, year);
        var totals = NannyYear.Sum(recorded);
        var projected = NannyYear.Projected(months, year, today, summary.Closed);
        return new NannyDetail(summary, year, NannyYear.Years(months, today),
            recorded.AsEnumerable().Reverse().Select(m => new NannyMonthView(Format(m.Month), Money(m.Paid), Money(m.Cmg), Money(m.NetCost),
                m.Hours is { } h ? ChildcareReader.Text(h) : null)).ToList(),
            Money(totals.Paid), Money(totals.Cmg), Money(totals.NetCost),
            projected.Count == 0 ? null : new NannyProjectionView(projected.Count, Money(projected[0].Amount), Money(totals.NetCost + projected.Sum(p => p.Amount))));
    }
}
