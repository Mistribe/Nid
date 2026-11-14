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
        return new(revisions, gaps, overrides, month, chosen, ContractOf(revisions, chosen));
    }

    /// <summary>The contract under the revision in force on this day: a revision taking effect mid-month prices that day's upkeep and meals.</summary>
    public ContractTerms On(LocalDate day)
    {
        var revision = Revisions.LastOrDefault(r => r.From <= day) ?? Revisions[0];
        return revision.From == Chosen.From ? Contract : ContractOf(Revisions, revision);
    }

    private static ContractTerms ContractOf(IReadOnlyList<Terms> revisions, Terms r)
        => new(revisions[0].From, revisions[^1].To, r.NetHourlyRate, r.OvertimeRate, r.UpkeepPer9h, r.LunchPrice,
            r.SnackPrice, r.LunchProvider, r.SnackProvider, r.UnpaidAbsences);
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

    /// <summary>The contract's last month, when it has ended: the month of its last day.</summary>
    public static bool IsLast(MonthTerms terms, ContractEnd? end) => end is not null && terms.Contract.End is { } last && last.ToYearMonth() == terms.Month;

    /// <summary>
    /// Whether this month's evaluation reads the days of the validated <paramref name="month"/>: every one for the last month of an
    /// ended contract, the reference period's (June N−1 … May N) for June, whose leave the days acquire. The store lists statements
    /// without their days: a caller loads these.
    /// </summary>
    public static bool ReadsDaysOf(MonthTerms terms, ContractEnd? end, YearMonth month)
        => IsLast(terms, end) || terms.Month.Month == 6 && month.CompareTo(terms.Month.PlusMonths(-12)) >= 0 && month.CompareTo(terms.Month) < 0;

    /// <summary>
    /// The figures of these days, or the problems and missing June months that withhold them. <paramref name="stored"/>: the
    /// arrangement's statements (the validated ones' figures count; with their days on the last month of an ended contract, see
    /// <see cref="IsLast"/>); <paramref name="previous"/>, <paramref name="next"/>: the adjacent months', with their days.
    /// </summary>
    public static MonthEvaluation Evaluate(
        MonthTerms terms, IReadOnlyList<StatementDay> days, IReadOnlyList<(string Field, string Code)> stale, IReadOnlyList<Statement> stored,
        Statement? previous, decimal mileage, decimal? cmg, DeclaredFigures? declared = null, decimal contributions = 0m, Statement? next = null, decimal? exemption = null,
        ContractEnd? end = null)
    {
        IReadOnlyList<(string Field, string Code)> problems = [.. StatementCheck.Days(terms.Month, days), .. stale];
        var validated = stored.Where(s => s.Validated && s.Figures is not null && s.Month.CompareTo(terms.Month) < 0).ToList();
        PaidLeaveInput? june = null;
        IReadOnlyList<YearMonth> missing = [];
        if (terms.Month.Month == 6)
            (june, missing) = StatementContext.June(terms.Month, terms.Contract, terms.Plan, terms.Gaps,
                validated.Select(s => (s.Month, s.Figures!.BasePay - s.Figures.Deduction + s.Figures.ExtraPay, s.Days)).ToList());
        EndingInput? ending = null;
        if (IsLast(terms, end))
        {
            (ending, var before) = Ending(terms, end!, validated);
            missing = [.. missing.Union(before).Order()];
            if (ending is not null && ending.GrossRatio is null && EndOfContract.IndemnityDue(end!.Reason, terms.Contract.Start, end.Notified))
                problems = [.. problems, ("terms.grossHourlyRate", ChildcareCodes.GrossRateRequired)];
        }
        if (problems.Count > 0 || missing.Count > 0)
            return new MonthEvaluation(null, problems, missing);

        var input = new MonthInput(terms.Month, terms.Contract, terms.Plan, terms.Gaps, days,
            [.. Neighbour(terms, terms.Month.PlusMonths(-1), previous), .. Neighbour(terms, terms.Month.PlusMonths(1), next)], StatementContext.ChildSickUsedBefore(terms.Contract.Start, terms.Month,
                validated.Select(s => (s.Month, s.Figures!.ChildSickDeducted)).ToList()),
            mileage, cmg ?? 0m, june, declared, contributions, exemption, terms.On, ending);
        return new MonthEvaluation(ChildcareMonth.Compute(input), problems, missing);
    }

    /// <summary>
    /// What the last month settles, from the validated months before it (each with its days), or the months still to validate.
    /// The regularisation runs over the whole contract (art. 124), the leave balance over the period since the last 1 June, the one
    /// June's statement has not paid (art. 125).
    /// </summary>
    private static (EndingInput? Input, IReadOnlyList<YearMonth> Missing) Ending(MonthTerms terms, ContractEnd end, IReadOnlyList<Statement> validated)
    {
        var (start, last) = (terms.Contract.Start, terms.Contract.End!.Value);
        var first = ChildcareRules.FirstDay.ToYearMonth();
        var earlier = StatementContext.Months(start, last, last).Where(m => m != terms.Month).ToList();
        var missing = earlier.Where(m => m.CompareTo(first) >= 0 && validated.All(v => v.Month != m)).ToList();
        if (missing.Count > 0)
            return (null, missing);

        var leaveFrom = LocalDate.Max(start, new LocalDate(terms.Month.Month >= 6 ? terms.Month.Year : terms.Month.Year - 1, 6, 1));
        var (due, paid, net, leavePay) = (0m, 0m, 0m, 0m);
        var leaveDays = new List<StatementDay>();
        foreach (var month in validated.Where(v => earlier.Contains(v.Month)).OrderBy(v => v.Month))
        {
            var f = month.Figures!;
            var monthTerms = MonthTerms.For(terms.Revisions, terms.Gaps, terms.Overrides, month.Month);
            if (month.Days.Count == 0)
                throw new ArgumentException($"{month.Month:uuuu-MM} is needed with its days.", nameof(validated));
            var sickBefore = StatementContext.ChildSickUsedBefore(start, month.Month, validated.Select(v => (v.Month, v.Figures!.ChildSickDeducted)).ToList());
            var inContract = month.Days.Where(d => StatementPreparation.InContract(d.Date, monthTerms.Contract)).ToList();
            due += Mensualisation.Round(monthTerms.Contract.NetHourlyRate * EndOfContract.HoursDue(inContract, monthTerms.Plan, sickBefore, monthTerms.Contract.UnpaidAbsences));
            paid += f.BasePay - f.Deduction;
            net += f.Declaration.NetPay;
            if (month.Month.CompareTo(leaveFrom.ToYearMonth()) >= 0)
            {
                leavePay += f.BasePay - f.Deduction + f.ExtraPay;
                leaveDays.AddRange(inContract.Where(d => d.Date >= leaveFrom));
            }
        }
        var gross = terms.Revisions.LastOrDefault(r => r.GrossHourlyRate is not null);
        return (new EndingInput(end.Reason, end.Notified, leaveDays, leavePay, due, paid, net,
            gross is null ? null : gross.GrossHourlyRate!.Value / gross.NetHourlyRate), []);
    }

    /// <summary>
    /// An adjacent month's days, prepared from this month's terms; its real days replace them when that month used the same
    /// revision and still fits the template (they decide the extra hours of the weeks the two months share).
    /// </summary>
    private static IReadOnlyList<StatementDay> Neighbour(MonthTerms terms, YearMonth month, Statement? stored)
    {
        var prepared = Prepared(terms, month);
        if (stored is not { } s || s.Month != month || MonthTerms.For(terms.Revisions, terms.Gaps, terms.Overrides, month).Chosen.From != terms.Chosen.From)
            return prepared;
        var days = s.Validated ? Replanned(terms, s.Days) : Overlay(terms, month, s.Days).Days;
        return StatementCheck.Days(month, days).Count == 0 ? days : prepared;
    }
}
