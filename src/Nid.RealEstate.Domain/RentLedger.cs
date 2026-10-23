using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using NodaTime;

namespace Nid.RealEstate.Domain;

/// <summary>One month of the ledger: what is due, what the cash imputed to it covers, and when the last of that cash came.</summary>
public sealed record LedgerMonth(LocalDate Month, decimal Due, decimal Imputed, decimal Remaining, LocalDate? LastPaidOn);

/// <summary>
/// The rent ledger at d: each tenancy owes from T, its tracked start (<see cref="Tracked"/>), to the end of d's month — rent is payable in
/// advance, so the current month is due whole; a month a lease starts or ends in is prorated. Every cash in dated on or before d is
/// imputed to the months due, oldest first.
/// </summary>
public static class RentLedger
{
    private static bool IsCash(Flow f) => f.Kind == FlowKind.Income && PropertyCategories.CashIn.Contains(f.Category!);

    private static LocalDate MonthOf(LocalDate d) => new(d.Year, d.Month, 1);

    private static LocalDate EndOfMonth(LocalDate d) => MonthOf(d).PlusMonths(1).PlusDays(-1);

    /// <summary>
    /// T: the later of the lease's start and the first rent tracked in Nid — <c>RentsTrackedFrom</c> when set; else the lease's start when
    /// the first cash in on or after it comes before the end of the lease's second month, the first day of that cash's month otherwise;
    /// without any such cash, d − 12 months + 1 day.
    /// </summary>
    public static LocalDate Tracked(Tenancy t, IReadOnlyList<Flow> flows, LocalDate d)
    {
        LocalDate from;
        if (t.RentsTrackedFrom is { } set)
            from = set;
        else if (flows.Where(f => IsCash(f) && f.OccurredOn >= t.StartsOn && f.OccurredOn <= d).Select(f => (LocalDate?)f.OccurredOn).Min() is { } first)
            from = first < MonthOf(t.StartsOn).PlusMonths(2) ? t.StartsOn : MonthOf(first);
        else
            from = d.PlusMonths(-12).PlusDays(1);
        return from > t.StartsOn ? from : t.StartsOn;
    }

    /// <summary>The earliest T of the tenancies (the last 12 months without any): no month before it is due.</summary>
    public static LocalDate Start(IReadOnlyList<Tenancy> tenancies, IReadOnlyList<Flow> flows, LocalDate d)
        => tenancies.Count == 0 ? d.PlusMonths(-12).PlusDays(1) : tenancies.Min(t => Tracked(t, flows, d));

    /// <summary>The days of the month a tenancy owes: those it covers from its T on; 0 is no slice of the ledger.</summary>
    public static bool Counts(Tenancy t, LocalDate tracked, LocalDate month)
        => PropertyMath.Covered(t, tracked > month ? tracked : month, EndOfMonth(month)) > 0;

    /// <summary>The tenancies started by d: a lease starting later, even this month, owes nothing yet.</summary>
    public static List<Tenancy> Started(IReadOnlyList<Tenancy> tenancies, LocalDate d) => tenancies.Where(t => t.StartsOn <= d).ToList();

    private static decimal Cash(IReadOnlyList<Flow> flows, LocalDate d) => flows.Where(f => IsCash(f) && f.OccurredOn <= d).Sum(f => f.Amount.Amount);

    /// <summary>The ledger's total due less all its cash at d: negative is a credit. <c>RentalFigures.Arrears</c>.</summary>
    public static decimal Arrears(IReadOnlyList<Tenancy> tenancies, IReadOnlyList<Flow> flows, LocalDate d)
        => Started(tenancies, d).Sum(t => PropertyMath.Expected(t, Tracked(t, flows, d), EndOfMonth(d))) - Cash(flows, d);

    /// <summary>
    /// The <c>rent_arrears</c> watch: the dues of the months that ended more than <paramref name="graceDays"/> days before d, less all the
    /// cash to d — rent recorded a few days late, or dated by the routine in the month it settles, is not flagged.
    /// </summary>
    public static decimal ArrearsDueBy(IReadOnlyList<Tenancy> tenancies, IReadOnlyList<Flow> flows, LocalDate d, int graceDays)
    {
        var ended = MonthOf(d.PlusDays(-graceDays)).PlusDays(-1); // the last month end strictly more than graceDays before d
        return Started(tenancies, d).Sum(t => PropertyMath.Expected(t, Tracked(t, flows, d), ended)) - Cash(flows, d);
    }

    /// <summary>The months some tenancy owes (the union of their slices), oldest first.</summary>
    public static IReadOnlyList<LedgerMonth> Months(IReadOnlyList<Tenancy> tenancies, IReadOnlyList<Flow> flows, LocalDate d)
    {
        var tracked = Started(tenancies, d).Select(t => (Tenancy: t, From: Tracked(t, flows, d))).ToList();
        var due = new List<(LocalDate Month, decimal Due)>();
        for (var month = MonthOf(Start(tenancies, flows, d)); month <= d; month = month.PlusMonths(1))
        {
            var slices = tracked.Where(x => Counts(x.Tenancy, x.From, month)).ToList();
            if (slices.Count > 0)
                due.Add((month, slices.Sum(x => PropertyMath.Expected(x.Tenancy, x.From > month ? x.From : month, EndOfMonth(month)))));
        }

        var imputed = new decimal[due.Count];
        var lastPaid = new LocalDate?[due.Count];
        var cash = flows
            .Where(f => IsCash(f) && f.OccurredOn <= d)
            .OrderBy(f => f.OccurredOn).ThenBy(f => f.Amount.Amount);
        var i = 0;
        foreach (var flow in cash)
        {
            var left = flow.Amount.Amount;
            for (; left > 0 && i < due.Count; i++)
            {
                var take = Math.Min(left, due[i].Due - imputed[i]);
                if (take > 0)
                {
                    imputed[i] += take;
                    lastPaid[i] = flow.OccurredOn;
                    left -= take;
                }
                if (imputed[i] < due[i].Due)
                    break; // this flow is spent on a month still short
            }
        }

        return due.Select((m, k) => new LedgerMonth(m.Month, m.Due, imputed[k], m.Due - imputed[k], lastPaid[k])).ToList();
    }
}

/// <summary>The figures of one month's receipt: a quittance when settled, a receipt for a partial payment, "unpaid" when nothing came.</summary>
public sealed record ReceiptFigures(LocalDate Month, string Kind, decimal Rent, decimal Charges, decimal Total, decimal Paid, LocalDate? PaidOn);

public static class RentReceipt
{
    private static LocalDate Evaluated(LocalDate month, LocalDate today) => today > month.PlusMonths(1).PlusDays(-1) ? today : month.PlusMonths(1).PlusDays(-1);

    /// <summary>The tenancies whose ledger slice covers a day of the month, the ledger read at max(today, the month's last day).</summary>
    private static List<Tenancy> Owing(IReadOnlyList<Tenancy> tenancies, IReadOnlyList<Flow> flows, LocalDate month, LocalDate today)
    {
        var d = Evaluated(month, today);
        return tenancies.Where(t => RentLedger.Counts(t, RentLedger.Tracked(t, flows, d), month)).ToList();
    }

    /// <summary>A receipt can be asked for: the month is not after today's and some tenancy's ledger slice covers a day of it.</summary>
    public static bool InRange(IReadOnlyList<Tenancy> tenancies, IReadOnlyList<Flow> flows, LocalDate month, LocalDate today)
    {
        month = new LocalDate(month.Year, month.Month, 1);
        return month <= today && Owing(RentLedger.Started(tenancies, today), flows, month, today).Count > 0;
    }

    /// <summary>Null when the month is after today's or no tenancy's ledger slice covers a day of it.</summary>
    public static ReceiptFigures? For(IReadOnlyList<Tenancy> tenancies, IReadOnlyList<Flow> flows, LocalDate month, LocalDate today)
    {
        month = new LocalDate(month.Year, month.Month, 1);
        tenancies = RentLedger.Started(tenancies, today);
        if (month > today || Owing(tenancies, flows, month, today) is not { Count: > 0 } covering)
            return null;
        var rent = covering.Sum(t => PropertyMath.RentIn(t, month, false)!.Value.Amount);
        var total = covering.Sum(t => PropertyMath.RentIn(t, month, true)!.Value.Amount);
        var ledger = RentLedger.Months(tenancies, flows, Evaluated(month, today)).First(m => m.Month == month);
        var kind = ledger.Remaining == 0 && ledger.Due > 0 ? "quittance" : ledger.Imputed > 0 ? "receipt" : "unpaid";
        return new ReceiptFigures(month, kind, rent, total - rent, total, ledger.Imputed, ledger.LastPaidOn);
    }
}
