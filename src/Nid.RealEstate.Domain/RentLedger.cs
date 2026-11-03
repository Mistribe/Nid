using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using NodaTime;

namespace Nid.RealEstate.Domain;

/// <summary>One month of the ledger: what is due, what the cash imputed to it covers, and when the last of that cash came.</summary>
public sealed record LedgerMonth(LocalDate Month, decimal Due, decimal Imputed, decimal Remaining, LocalDate? LastPaidOn);

/// <summary>
/// One tenant's account (rental receipts spec §2): a revision chain's tenancies, oldest first, and the cash in attributed to it by date
/// (<see cref="RentLedger.Accounts"/>). <c>Chain</c> is the chain's last link.
/// </summary>
public sealed record RentAccount(Guid Chain, IReadOnlyList<Tenancy> Tenancies, IReadOnlyList<Flow> Cash);

/// <summary>
/// The rent ledger at d: each tenancy owes from T, its tracked start (<see cref="Tracked"/>), to the end of d's month — rent is payable in
/// advance, so the current month is due whole; a month a lease starts or ends in is prorated. Every cash in dated on or before d is
/// imputed to the months due, oldest first. The functions over a list of tenancies read it as one account; a property has one per
/// tenant (<see cref="Accounts"/>).
/// </summary>
public static class RentLedger
{
    private static bool IsCash(Flow f) => f.Kind == FlowKind.Income && PropertyCategories.CashIn.Contains(f.Category!);

    private static LocalDate MonthOf(LocalDate d) => new(d.Year, d.Month, 1);

    private static LocalDate EndOfMonth(LocalDate d) => MonthOf(d).PlusMonths(1).PlusDays(-1);

    /// <summary>
    /// T: the later of the lease's start and the first rent tracked in Nid — <c>RentsTrackedFrom</c> when set; else the lease's start when
    /// the first cash in on or after it comes before the end of the lease's second month, the first day of that cash's month otherwise;
    /// without any such cash, d − 12 months + 1 day — a fallback for a lease still running: a former tenant without any cash owes nothing (<see cref="Started"/>).
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

    /// <summary>
    /// The tenancies that owe at d: those started by d (a lease starting later, even this month, owes nothing yet); none for a former
    /// tenant — every lease ended by d — without any cash in to d nor « Loyers suivis depuis »: rents never recorded in Nid are not owed.
    /// </summary>
    public static List<Tenancy> Started(IReadOnlyList<Tenancy> tenancies, IReadOnlyList<Flow> flows, LocalDate d)
        => tenancies.All(t => t.EndsOn is { } end && end < d && t.RentsTrackedFrom is null) && !flows.Any(f => IsCash(f) && f.OccurredOn <= d)
            ? []
            : tenancies.Where(t => t.StartsOn <= d).ToList();

    private static decimal Cash(IReadOnlyList<Flow> flows, LocalDate d) => flows.Where(f => IsCash(f) && f.OccurredOn <= d).Sum(f => f.Amount.Amount);

    /// <summary>The ledger's total due less all its cash at d: negative is a credit. <c>RentalFigures.Arrears</c>.</summary>
    public static decimal Arrears(IReadOnlyList<Tenancy> tenancies, IReadOnlyList<Flow> flows, LocalDate d)
        => Started(tenancies, flows, d).Sum(t => PropertyMath.Expected(t, Tracked(t, flows, d), EndOfMonth(d))) - Cash(flows, d);

    /// <summary>
    /// The <c>rent_arrears</c> watch: the dues of the months that ended more than <paramref name="graceDays"/> days before d, less all the
    /// cash to d — rent recorded a few days late, or dated by the routine in the month it settles, is not flagged.
    /// </summary>
    public static decimal ArrearsDueBy(IReadOnlyList<Tenancy> tenancies, IReadOnlyList<Flow> flows, LocalDate d, int graceDays)
    {
        var ended = MonthOf(d.PlusDays(-graceDays)).PlusDays(-1); // the last month end strictly more than graceDays before d
        return Started(tenancies, flows, d).Sum(t => PropertyMath.Expected(t, Tracked(t, flows, d), ended)) - Cash(flows, d);
    }

    /// <summary>A payment dated in the <see cref="AdvanceDays"/> days before a tenant's first start, at signing, is that incoming tenant's.</summary>
    public const int AdvanceDays = 15;

    /// <summary>
    /// One account per tenant: per revision chain (<c>RevisedFrom</c> links; a lone tenancy is its own), ordered by first start. A cash in dated
    /// D goes to the chain starting within <see cref="AdvanceDays"/> days after D (D in [start − 15 days, start)), else to the chain whose
    /// tenancy covers D (the latest started if two), else to the chain that ended last before D, else (before any lease) to the first chain.
    /// ponytail: a flow does not say who paid — a former tenant's payment dated after the next one moved in, or in the 15 days before,
    /// counts for the next one.
    /// </summary>
    public static IReadOnlyList<RentAccount> Accounts(IReadOnlyList<(Guid Id, Guid? RevisedFrom, Tenancy Tenancy)> tenancies, IReadOnlyList<Flow> flows)
    {
        var chains = tenancies.GroupBy(t => LastLink(tenancies, t.Id))
            .Select(g => (Chain: g.Key, Tenancies: g.Select(t => t.Tenancy).OrderBy(t => t.StartsOn).ToList()))
            .OrderBy(c => c.Tenancies[0].StartsOn).ToList();
        if (chains.Count == 0)
            return [];
        var cash = chains.Select(_ => new List<Flow>()).ToList();
        foreach (var flow in flows.Where(IsCash))
        {
            var on = flow.OccurredOn;
            var all = Enumerable.Range(0, chains.Count);
            var owner = all.FirstOrDefault(k => on >= chains[k].Tenancies[0].StartsOn.PlusDays(-AdvanceDays) && on < chains[k].Tenancies[0].StartsOn, -1);
            if (owner < 0)
                owner = all.LastOrDefault(k => chains[k].Tenancies.Any(t => PropertyMath.Covers(t, on)), -1);
            if (owner < 0)
                owner = all.Where(k => chains[k].Tenancies[0].StartsOn <= on).DefaultIfEmpty(0)
                    .MaxBy(k => chains[k].Tenancies.Max(t => t.EndsOn ?? LocalDate.MaxIsoValue));
            cash[owner].Add(flow);
        }
        return chains.Select((c, k) => new RentAccount(c.Chain, c.Tenancies, cash[k])).ToList();
    }

    /// <summary>The last link of the revision chain of <paramref name="id"/>.</summary>
    public static Guid LastLink(IReadOnlyList<(Guid Id, Guid? RevisedFrom, Tenancy Tenancy)> tenancies, Guid id)
    {
        // Bounded: a cyclic RevisedFrom must not hang. Two links sharing one RevisedFrom cannot come from the app: the first found is kept.
        for (var hop = 0; hop < tenancies.Count && tenancies.Where(t => t.RevisedFrom == id).Select(t => (Guid?)t.Id).FirstOrDefault() is { } next; hop++)
            id = next;
        return id;
    }

    /// <summary>
    /// <c>RentalFigures.Arrears</c>: what the tenants owe, account by account — a former tenant's credit never hides the current one's debt;
    /// with nothing owed on any account, the signed total (a credit: « plus perçu que prévu »).
    /// </summary>
    public static decimal PropertyArrears(IReadOnlyList<(Guid Id, Guid? RevisedFrom, Tenancy Tenancy)> tenancies, IReadOnlyList<Flow> flows, LocalDate d)
    {
        var each = Accounts(tenancies, flows).Select(a => Arrears(a.Tenancies, a.Cash, d)).ToList();
        var owed = each.Sum(x => Math.Max(0m, x));
        return owed > 0m ? owed : each.Count == 0 ? -Cash(flows, d) : each.Sum();
    }

    /// <summary>The <c>rent_arrears</c> watch: <see cref="ArrearsDueBy"/> account by account, credits left out.</summary>
    public static decimal PropertyArrearsDueBy(
        IReadOnlyList<(Guid Id, Guid? RevisedFrom, Tenancy Tenancy)> tenancies, IReadOnlyList<Flow> flows, LocalDate d, int graceDays)
        => Accounts(tenancies, flows).Sum(a => Math.Max(0m, ArrearsDueBy(a.Tenancies, a.Cash, d, graceDays)));

    /// <summary>The months some tenancy owes (the union of their slices), oldest first.</summary>
    public static IReadOnlyList<LedgerMonth> Months(IReadOnlyList<Tenancy> tenancies, IReadOnlyList<Flow> flows, LocalDate d)
    {
        var tracked = Started(tenancies, flows, d).Select(t => (Tenancy: t, From: Tracked(t, flows, d))).ToList();
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

/// <summary>A ledger month still owing: Overdue once it ended strictly more than GraceDays days before today; Chain is the last link of its tenant's revision chain.</summary>
public sealed record ArrearsMonth(LocalDate Month, decimal Due, decimal Imputed, decimal Remaining, bool Overdue, Guid? Chain);

/// <summary>The months to chase: each account's ledger months with a remainder, built on <see cref="RentLedger.Months"/> (no imputation of its own).</summary>
public static class RentArrears
{
    /// <summary>The watch's grace, shared with <see cref="RentLedger.ArrearsDueBy"/>.</summary>
    public const int GraceDays = 10;

    /// <summary>By month, then by tenant: a changeover month both tenants owe is two rows, each of its own days.</summary>
    public static IReadOnlyList<ArrearsMonth> Of(IReadOnlyList<(Guid Id, Guid? RevisedFrom, Tenancy Tenancy)> tenancies, IReadOnlyList<Flow> flows, LocalDate today)
    {
        var graced = today.PlusDays(-GraceDays);
        var firstNotOverdue = new LocalDate(graced.Year, graced.Month, 1); // the month ArrearsDueBy stops before
        return RentLedger.Accounts(tenancies, flows)
            .SelectMany(a => RentLedger.Months(a.Tenancies, a.Cash, today).Where(m => m.Remaining > 0)
                .Select(m => new ArrearsMonth(m.Month, m.Due, m.Imputed, m.Remaining, m.Month < firstNotOverdue, a.Chain)))
            .OrderBy(m => m.Month) // stable: the accounts' order within a month
            .ToList();
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
        return month <= today && Owing(RentLedger.Started(tenancies, flows, today), flows, month, today).Count > 0;
    }

    /// <summary>Null when the month is after today's or no tenancy's ledger slice covers a day of it.</summary>
    public static ReceiptFigures? For(IReadOnlyList<Tenancy> tenancies, IReadOnlyList<Flow> flows, LocalDate month, LocalDate today)
    {
        month = new LocalDate(month.Year, month.Month, 1);
        tenancies = RentLedger.Started(tenancies, flows, today);
        if (month > today || Owing(tenancies, flows, month, today) is not { Count: > 0 } covering)
            return null;
        var rent = covering.Sum(t => PropertyMath.RentIn(t, month, false)!.Value.Amount);
        var total = covering.Sum(t => PropertyMath.RentIn(t, month, true)!.Value.Amount);
        var ledger = RentLedger.Months(tenancies, flows, Evaluated(month, today)).First(m => m.Month == month);
        var kind = ledger.Remaining == 0 && ledger.Due > 0 ? "quittance" : ledger.Imputed > 0 ? "receipt" : "unpaid";
        return new ReceiptFigures(month, kind, rent, total - rent, total, ledger.Imputed, ledger.LastPaidOn);
    }
}
