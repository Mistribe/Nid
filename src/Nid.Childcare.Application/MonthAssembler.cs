using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using NodaTime;

namespace Nid.Childcare.Application;

/// <summary>Decision 12: a month's revision of the arrangement and the contract it is computed under (the arrangement's own dates).</summary>
public sealed record MonthTerms(
    IReadOnlyList<Terms> Revisions, IReadOnlyList<CareGap> Gaps, IReadOnlyList<WeekOverride> Overrides, YearMonth Month, Terms Chosen, ContractTerms Contract)
{
    /// <summary>The chosen revision's week types over the contract's rotation and exceptions; a getter, so <c>with { Chosen = … }</c> keeps it right.</summary>
    public WeekPlan Plan => TermsPlan.Of(Chosen, Revisions[0].From, Overrides);

    public static MonthTerms For(IReadOnlyList<Terms> revisions, IReadOnlyList<CareGap> gaps, IReadOnlyList<WeekOverride> overrides, YearMonth month)
    {
        var chosen = revisions[StatementContext.TermsIndexFor(month, revisions[0].From, revisions.Select(r => (r.From, r.To)).ToList())];
        return new(revisions, gaps, overrides, month, chosen,
            new ContractTerms(revisions[0].From, revisions[^1].To, chosen.NetHourlyRate, chosen.OvertimeRate, chosen.UpkeepPer9h, chosen.MealPrice));
    }
}

/// <summary>
/// Childcare decisions 12–16 and 25: a month's days and figures from data in memory — the store's (<see cref="StatementAssembly"/>) or an
/// import file's, before anything is written. Pure.
/// </summary>
public static class MonthAssembler
{
    public static IReadOnlyList<StatementDay> Prepared(MonthTerms terms, YearMonth? month = null)
        => StatementPreparation.Prepare(month ?? terms.Month, terms.Plan, terms.Gaps, terms.Contract);

    public static IReadOnlyList<StatementDay> Replanned(MonthTerms terms, IReadOnlyList<StatementDay> days)
        => days.Select(d => d with { Planned = StatementPreparation.IsPlanned(d.Date, terms.Plan, terms.Gaps, terms.Contract) }).ToList();

    /// <summary>Decision 14: a draft keeps only its corrections; every other day follows the template, a correction whose plan changed is stale.</summary>
    public static (IReadOnlyList<StatementDay> Days, IReadOnlyList<(string Field, string Code)> Stale) Overlay(
        MonthTerms terms, YearMonth month, IReadOnlyList<StatementDay> corrections)
    {
        var prepared = Prepared(terms, month);
        var byDate = corrections.ToDictionary(d => d.Date);
        var days = new List<StatementDay>();
        var stale = new List<(string, string)>();
        for (var i = 0; i < prepared.Count; i++)
        {
            if (!byDate.TryGetValue(prepared[i].Date, out var correction))
            {
                days.Add(prepared[i]);
                continue;
            }
            days.Add(correction with { Planned = prepared[i].Planned });
            if (correction.Planned != prepared[i].Planned) stale.Add(($"days[{i}].planned", ChildcareCodes.DayReplanned));
        }
        return (days, stale);
    }

    /// <summary>
    /// The figures of these days, or the problems and missing June months that withhold them. <paramref name="stored"/>: the
    /// arrangement's statements (the validated ones' figures count); <paramref name="previous"/>: the previous month's, with its days.
    /// </summary>
    public static MonthEvaluation Evaluate(
        MonthTerms terms, IReadOnlyList<StatementDay> days, IReadOnlyList<(string Field, string Code)> stale, IReadOnlyList<Statement> stored,
        Statement? previous, decimal mileage, decimal? cmg)
    {
        IReadOnlyList<(string Field, string Code)> problems = [.. StatementCheck.Days(terms.Month, days), .. stale];
        var validated = stored.Where(s => s.Validated && s.Figures is not null && s.Month.CompareTo(terms.Month) < 0).ToList();
        PaidLeaveInput? june = null;
        IReadOnlyList<YearMonth> missing = [];
        if (terms.Month.Month == 6)
            (june, missing) = StatementContext.June(terms.Month, terms.Contract.Start, terms.Contract.End,
                validated.Select(s => (s.Month, s.Figures!.BasePay - s.Figures.Deduction + s.Figures.ExtraPay)).ToList());
        if (problems.Count > 0 || missing.Count > 0)
            return new MonthEvaluation(null, problems, missing);

        var input = new MonthInput(terms.Month, terms.Contract, terms.Plan, terms.Gaps, days,
            Neighbours(terms, previous), StatementContext.ChildSickUsedBefore(terms.Contract.Start, terms.Month,
                validated.Select(s => (s.Month, s.Figures!.ChildSickDeducted)).ToList()),
            mileage, cmg ?? 0m, june);
        return new MonthEvaluation(ChildcareMonth.Compute(input), problems, missing);
    }

    /// <summary>
    /// The adjacent months' days, prepared from this month's terms; the previous month's days replace them when that month used the
    /// same revision and still fits the template (they decide the first week's extra hours).
    /// </summary>
    private static IReadOnlyList<StatementDay> Neighbours(MonthTerms terms, Statement? previous)
    {
        var month = terms.Month.PlusMonths(-1);
        var before = Prepared(terms, month);
        var sameTerms = MonthTerms.For(terms.Revisions, terms.Gaps, terms.Overrides, month).Chosen.From == terms.Chosen.From;
        if (sameTerms && previous is { } p && p.Month == month)
        {
            var days = p.Validated ? Replanned(terms, p.Days) : Overlay(terms, month, p.Days).Days;
            if (StatementCheck.Days(month, days).Count == 0)
                before = days;
        }
        return [.. before, .. Prepared(terms, terms.Month.PlusMonths(1))];
    }
}
