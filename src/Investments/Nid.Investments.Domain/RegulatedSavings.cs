using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Investments.Domain;

/// <summary>A rule in force from <c>From</c> until the next one of its kind, and where it comes from.</summary>
public sealed record DatedRule(LocalDate From, decimal Value, string Source);

/// <summary>The interest of a year so far (whole fortnights up to the date) and on 31 December, at the regulated rates. Estimates.</summary>
public sealed record InterestEstimate(int Year, decimal ToDate, decimal Expected, decimal RatePct, string Source);

/// <summary>
/// Issues #399, #401: the regulated livrets' ceilings and rates, as dated rules (one table for both). Ceilings are on deposits:
/// interest can push a balance over (« hors capitalisation des intérêts »). The livret jeune's rate is the bank's own (at least the
/// Livret A's): no regulated rate, no estimate.
/// </summary>
public static class RegulatedSavings
{
    private const string Framework = "Banque de France, Épargne réglementée — cadre juridique (juin 2026)";
    private const string History = "Banque de France, historique des taux de l'épargne réglementée";
    private const string August2026 = "Ministère de l'Économie, taux au 1er août 2026 (annonce du 15 juillet 2026)";

    private static readonly IReadOnlyDictionary<RegulatedKind, DatedRule[]> Ceilings = new Dictionary<RegulatedKind, DatedRule[]>
    {
        [RegulatedKind.LivretA] =
        [
            new(new LocalDate(2012, 10, 1), 19_125m, Framework + ", CMF art. R. 221-2"),
            new(new LocalDate(2013, 1, 1), 22_950m, Framework + ", CMF art. R. 221-2"),
        ],
        [RegulatedKind.Ldds] = [new(new LocalDate(2012, 10, 1), 12_000m, Framework + ", CMF art. D. 221-103 et s.")],
        [RegulatedKind.Lep] = [new(new LocalDate(2023, 10, 1), 10_000m, Framework + ", CMF art. R. 221-33 et s.")],
        // ponytail: dated at the framework's own date, the earlier start not checked; an older date has no ceiling.
        [RegulatedKind.LivretJeune] = [new(new LocalDate(2026, 6, 1), 1_600m, Framework)],
    };

    /// <summary>Rates in percent a year; the LDDS's is the Livret A's (since 2003).</summary>
    private static readonly DatedRule[] LivretARates =
    [
        new(new LocalDate(2020, 2, 1), 0.5m, History),
        new(new LocalDate(2022, 2, 1), 1m, History),
        new(new LocalDate(2022, 8, 1), 2m, History),
        new(new LocalDate(2023, 2, 1), 3m, History),
        new(new LocalDate(2025, 2, 1), 2.4m, Framework),
        new(new LocalDate(2025, 8, 1), 1.7m, Framework),
        new(new LocalDate(2026, 2, 1), 1.5m, Framework),
        new(new LocalDate(2026, 8, 1), 1.7m, August2026),
    ];

    private static readonly IReadOnlyDictionary<RegulatedKind, DatedRule[]> Rates = new Dictionary<RegulatedKind, DatedRule[]>
    {
        [RegulatedKind.LivretA] = LivretARates,
        [RegulatedKind.Ldds] = LivretARates,
        [RegulatedKind.Lep] =
        [
            new(new LocalDate(2020, 2, 1), 1m, History),
            new(new LocalDate(2022, 2, 1), 2.2m, History),
            new(new LocalDate(2022, 8, 1), 4.6m, History),
            new(new LocalDate(2023, 2, 1), 6.1m, History),
            new(new LocalDate(2023, 8, 1), 6m, History),
            new(new LocalDate(2024, 2, 1), 5m, History),
            new(new LocalDate(2024, 8, 1), 4m, History),
            new(new LocalDate(2025, 2, 1), 3.5m, Framework),
            new(new LocalDate(2025, 8, 1), 2.7m, Framework),
            new(new LocalDate(2026, 2, 1), 2.5m, Framework),
            new(new LocalDate(2026, 8, 1), 2.5m, August2026),
        ],
    };

    /// <summary>The ceiling in force on the date; null for a kind or a date the table does not cover. A later date carries the last rule forward.</summary>
    public static DatedRule? Ceiling(RegulatedKind kind, LocalDate on) => InForce(Ceilings.GetValueOrDefault(kind), on);

    public static DatedRule? Rate(RegulatedKind kind, LocalDate on) => InForce(Rates.GetValueOrDefault(kind), on);

    private static DatedRule? InForce(DatedRule[]? rules, LocalDate on) => rules?.LastOrDefault(r => r.From <= on);

    /// <summary>
    /// The balance at the end of <paramref name="d"/>: the nearest valuation on or before it plus the money in and out since, or, before the
    /// first valuation, that valuation minus what came in between; with no valuation, the money in and out alone. Null with neither.
    /// Income and expense flows are left out: the valuations carry the interest.
    /// </summary>
    public static decimal? BalanceAt(IReadOnlyList<Valuation> values, IReadOnlyList<Flow> flows, LocalDate d)
    {
        var anchor = values.Where(v => v.AsOf <= d).MaxBy(v => v.AsOf) ?? values.Where(v => v.AsOf > d).MinBy(v => v.AsOf);
        if (anchor is null)
            return flows.Count == 0 ? null : Net(flows, f => f.OccurredOn <= d);
        return anchor.AsOf <= d
            ? anchor.Value.Amount + Net(flows, f => f.OccurredOn > anchor.AsOf && f.OccurredOn <= d)
            : anchor.Value.Amount - Net(flows, f => f.OccurredOn > d && f.OccurredOn <= anchor.AsOf);
    }

    /// <summary>
    /// Issue #401, the fortnight rule (service-public.gouv.fr F2365): interest runs by whole fortnights, from the 1st and the 16th. A deposit
    /// counts from the next fortnight (made 1–15 May: from 16 May; 16–31 May: from 1 June); a withdrawal stops earning from the start of its
    /// own fortnight (1–15 May: balance stopped on 30 April; 16–31 May: on 15 May). Each fortnight earns rate / 24 at the rate in force on its
    /// first day. <c>ToDate</c> counts the fortnights over by <paramref name="today"/>; <c>Expected</c> the year's 24, with no money in or out
    /// after today, so a flow already recorded with a later date is left out of both. Null without a regulated rate for every fortnight, or
    /// without a balance.
    /// </summary>
    public static InterestEstimate? Interest(RegulatedKind kind, IReadOnlyList<Valuation> values, IReadOnlyList<Flow> flows, LocalDate today)
    {
        flows = flows.Where(f => f.OccurredOn <= today).ToList();
        if (BalanceAt(values, flows, today) is null) return null;
        var year = today.Year;
        decimal toDate = 0m, expected = 0m;
        for (var month = 1; month <= 12; month++)
            foreach (var (start, end) in new[] { (new LocalDate(year, month, 1), new LocalDate(year, month, 15)),
                         (new LocalDate(year, month, 16), new LocalDate(year, month, 1).PlusMonths(1).PlusDays(-1)) })
            {
                if (Rate(kind, start) is not { } rate) return null;
                var balance = BalanceAt(values, flows, start.PlusDays(-1))!.Value
                    - Total(flows, f => f.Kind == FlowKind.Withdrawal && f.OccurredOn >= start && f.OccurredOn <= end);
                var earned = Math.Max(0m, balance) * rate.Value / 100m / 24m;
                expected += earned;
                if (end < today) toDate += earned;
            }
        var now = Rate(kind, today)!;
        return new InterestEstimate(year, Cents(toDate), Cents(expected), now.Value, now.Source);
    }

    private static decimal Cents(decimal x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);

    private static decimal Net(IReadOnlyList<Flow> flows, Func<Flow, bool> window)
        => Total(flows, f => window(f) && f.Kind is FlowKind.Opening or FlowKind.Contribution) - Total(flows, f => window(f) && f.Kind == FlowKind.Withdrawal);

    private static decimal Total(IReadOnlyList<Flow> flows, Func<Flow, bool> which) => flows.Where(which).Sum(f => f.Amount.Amount);
}

/// <summary>Issue #400: the next date that changes what a withdrawal costs; the date only, never a figure of tax saved.</summary>
public sealed record TaxMilestone(string Code, LocalDate On);

public static class TaxMilestones
{
    public const string LifeInsurance8Years = "life_insurance_8y";
    public const string Pea5Years = "pea_5y";
    public const string PerRetirement = "per_retirement";

    /// <summary>
    /// 8 years for a life insurance, 5 for a PEA, from the opening or the first deposit, whichever is earlier (the tax counts from the first
    /// premium or deposit); the owner's retirement date for a PER, when known. Null for another kind, without a start, or once past.
    /// </summary>
    public static TaxMilestone? Next(EnvelopeKind kind, LocalDate? openedOn, IReadOnlyList<Flow> flows, LocalDate? retirement, LocalDate today)
    {
        var firstDeposit = flows.Where(f => f.Kind is FlowKind.Opening or FlowKind.Contribution).Select(f => (LocalDate?)f.OccurredOn).Min();
        LocalDate? start = openedOn is { } o && firstDeposit is { } d ? (o < d ? o : d) : openedOn ?? firstDeposit;
        var milestone = kind switch
        {
            EnvelopeKind.LifeInsurance when start is { } s => new TaxMilestone(LifeInsurance8Years, s.PlusYears(8)),
            EnvelopeKind.Pea when start is { } s => new TaxMilestone(Pea5Years, s.PlusYears(5)),
            EnvelopeKind.Per when retirement is { } r => new TaxMilestone(PerRetirement, r),
            _ => null,
        };
        return milestone is { } m && m.On >= today ? m : null;
    }
}
