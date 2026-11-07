using System.Globalization;
using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Childcare.Application;

/// <summary>What a month's statement is read and written against (decisions 12–16).</summary>
public sealed record MonthContext(
    Arrangement Arrangement, ItemAccess Item, IReadOnlyList<Terms> Revisions, IReadOnlyList<CareGap> Gaps, IReadOnlyList<WeekOverride> Overrides, IReadOnlyList<Statement> Stored,
    LocalDate Today, YearMonth Month, Terms Chosen, ContractTerms Contract)
{
    public Statement? StoredMonth => Stored.FirstOrDefault(s => s.Month == Month);

    public MonthTerms Terms => new(Revisions, Gaps, Overrides, Month, Chosen, Contract);
}

/// <summary>A month's figures, or why there are none.</summary>
public sealed record MonthEvaluation(MonthFigures? Figures, IReadOnlyList<(string Field, string Code)> Problems, IReadOnlyList<YearMonth> Missing);

/// <summary>Spec §5 « Relevé du mois »: builds a month's days and figures from the store and the Domain, shared by queries and commands.</summary>
public sealed class StatementAssembly(IChildcareStore store, IItemRegistry items, IHouseholdSettings household, IHouseholdCalendar calendar, IAccessPolicy policy)
{
    private static readonly YearMonthPattern MonthPattern = YearMonthPattern.CreateWithInvariantCulture("uuuu-MM");

    public static string Format(YearMonth month) => MonthPattern.Format(month);

    public async Task<Result<(Arrangement Arrangement, ItemAccess Item, IReadOnlyList<Terms> Revisions, IReadOnlyList<Statement> Stored, LocalDate Today)>> ArrangementAsync(
        ItemId id, CancellationToken ct)
    {
        var loaded = await ChildcareCheck.LoadAsync(store, items, id, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<(Arrangement, ItemAccess, IReadOnlyList<Terms>, IReadOnlyList<Statement>, LocalDate)>(failure!);
        loaded.TryGetValue(out var found);
        return Result.Success((found.Arrangement, found.Item, found.Terms, await store.StatementsOfAsync(id, ct), await calendar.TodayAsync(ct)));
    }

    /// <summary>The month's context; a month outside <see cref="StatementContext.Months"/> is not found.</summary>
    public async Task<Result<MonthContext>> ContextAsync(ItemId id, YearMonth month, CancellationToken ct)
    {
        var loaded = await ArrangementAsync(id, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<MonthContext>(failure!);
        loaded.TryGetValue(out var found);
        var (arrangement, item, revisions, stored, today) = found;
        if (!StatementContext.Months(revisions[0].From, revisions[^1].To, today).Contains(month))
            return Result.Failure<MonthContext>(new NotFoundFailure("childcare.statement", Format(month)));
        var overrides = (await store.OverridesOfAsync([id], ct)).Select(o => o.Override).ToList();
        var terms = MonthTerms.For(revisions, (await store.GapsOfAsync([id], ct)).Select(g => g.Gap).ToList(), overrides, month);
        return Result.Success(new MonthContext(arrangement, item, revisions, terms.Gaps, terms.Overrides, stored, today, month, terms.Chosen, terms.Contract));
    }

    /// <summary>
    /// Decision 14: a validated month's days as stored; a draft's corrections over the template as it now is, each correction
    /// whose plan changed since it was saved in <c>Stale</c>; otherwise the template's.
    /// </summary>
    public async Task<(IReadOnlyList<StatementDay> Days, IReadOnlyList<(string Field, string Code)> Stale)> DaysAsync(MonthContext context, CancellationToken ct)
    {
        if (context.StoredMonth is null)
            return (Prepared(context), []);
        var stored = (await store.FindStatementAsync(context.Arrangement.ItemId, context.Month, ct))!;
        return stored.Validated ? (stored.Days, []) : Overlay(context, context.Month, stored.Days);
    }

    public (IReadOnlyList<StatementDay> Days, IReadOnlyList<(string Field, string Code)> Stale) Overlay(MonthContext context, YearMonth month, IReadOnlyList<StatementDay> corrections)
        => MonthAssembler.Overlay(context.Terms, month, corrections);

    public IReadOnlyList<StatementDay> Prepared(MonthContext context) => MonthAssembler.Prepared(context.Terms);

    public IReadOnlyList<StatementDay> Replanned(MonthContext context, Terms terms, IReadOnlyList<StatementDay> days)
        => MonthAssembler.Replanned(context.Terms with { Chosen = terms }, days);

    /// <summary>The figures of these days, or the problems and missing June months that withhold them.</summary>
    public async Task<MonthEvaluation> EvaluateAsync(
        MonthContext context, IReadOnlyList<StatementDay> days, IReadOnlyList<(string Field, string Code)> stale, decimal mileage, decimal? cmg, DeclaredFigures? declared, CancellationToken ct)
    {
        var previous = context.Month.PlusMonths(-1);
        var stored = context.Stored.Any(s => s.Month == previous) ? await store.FindStatementAsync(context.Arrangement.ItemId, previous, ct) : null;
        return MonthAssembler.Evaluate(context.Terms, days, stale, context.Stored, stored, mileage, cmg, declared);
    }

    public async Task<ChildcareStatementView> ViewAsync(PersonId reader, MonthContext context, CancellationToken ct)
    {
        var stored = context.StoredMonth;
        var (days, stale) = await DaysAsync(context, ct);
        var currency = await household.ReferenceCurrencyAsync(ct);
        var canWrite = (await policy.CheckManyAsync(reader, [context.Arrangement.ItemId], AccessKind.Write, ct)).Contains(context.Arrangement.ItemId);
        MonthEvaluation evaluation = stored is { Validated: true, Figures: { } frozen }
            ? new(frozen, [], [])
            : await EvaluateAsync(context, days, stale, stored?.Mileage ?? 0m, stored?.Cmg, stored?.Declared, ct);
        // #32: Nid's own figures are the month's without the fields entered by hand; a validated month's days follow the template as it now is.
        var computed = stored?.Declared is not { } typed || typed == new DeclaredFigures() || evaluation.Figures is null
            ? evaluation.Figures
            : (await EvaluateAsync(context, stored!.Validated ? MonthAssembler.Replanned(context.Terms, days) : days, stale, stored.Mileage, stored.Cmg, null, ct)).Figures;
        var next = context.Month.PlusMonths(1);
        var prepared = Prepared(context);
        var plan = context.Terms.Plan;
        return new ChildcareStatementView(
            context.Arrangement.ItemId.Value, context.Item.DisplayName, Format(context.Month),
            stored is null ? "none" : stored.Validated ? "validated" : "draft",
            days.Select((d, i) => DayView(d, plan, i < prepared.Count ? prepared[i] : null)).ToList(),
            Amount(stored?.Mileage ?? 0m), stored?.Cmg is { } c ? Amount(c) : null,
            evaluation.Figures is { } f ? FiguresView(f, currency, stored?.Cmg is null) : null,
            evaluation.Problems.Select(p => new FieldProblemView(p.Field, p.Code)).ToList(),
            evaluation.Missing.Select(Format).ToList(),
            canWrite, canWrite && stored is { Validated: true } && !context.Stored.Any(s => s.Month == next && s.Validated),
            stored?.Version ?? 0,
            StatementContext.CmgEstimate(context.Stored.Where(s => s is { Validated: true, Figures: not null }).Select(s => (s.Month, s.Figures!.Cmg)).ToList()) is { } estimate
                ? Amount(estimate) : null,
            stored?.Declared is { } entered ? DeclaredOf(entered) : null,
            computed is { } n ? DeclarationOf(n.Declaration) : null);
    }

    public async Task<ChildcareStatementList> ListAsync(ItemId id, IReadOnlyList<Terms> revisions, IReadOnlyList<Statement> stored, LocalDate today, CancellationToken ct)
    {
        var currency = await household.ReferenceCurrencyAsync(ct);
        return new ChildcareStatementList(id.Value, StatementContext.Months(revisions[0].From, revisions[^1].To, today).Select(m =>
        {
            var s = stored.FirstOrDefault(x => x.Month == m);
            return new ChildcareStatementRow(Format(m), s is null ? "none" : s.Validated ? "validated" : "draft",
                s?.Figures is { } f ? ChildcareMoneyView.Of(f.Cost, currency) : null);
        }).ToList());
    }

    public static string Message(string code) => code switch
    {
        ChildcareCodes.DaysIncomplete => "Each day of the month once.",
        ChildcareCodes.DepartureBeforeArrival => "A present day needs an arrival before its departure.",
        ChildcareCodes.AbsenceRequired => "An absent planned day needs a reason.",
        ChildcareCodes.AbsenceNotAllowed => "A reason goes on an absent planned day only.",
        ChildcareCodes.DayReplanned => "The template or the weeks without care changed this day since it was saved.",
        ChildcareCodes.SummaryInvalid => "Care days and hours go together, and each day must fit within the day.",
        ChildcareCodes.SummaryTooManyDays => "The month has fewer working days than that.",
        _ => ChildcareCheck.Message(code),
    };

    /// <summary><c>WeekType</c>: the week's name, only with several week types and on Mondays and the month's first day.</summary>
    private static StatementDayView DayView(StatementDay d, WeekPlan plan, StatementDay? prepared)
    {
        var slot = d.Planned ? plan.SlotOn(d.Date) : null;
        var name = plan.Types.Count > 1 && (d.Date.DayOfWeek == IsoDayOfWeek.Monday || d.Date.Day == 1) ? plan.Types[plan.TypeOf(d.Date)].Name : null;
        return new StatementDayView(d.Date, d.Planned, d.Present, Time(d.Arrival), Time(d.Departure), d.Meal,
            d.Absence is { } a ? AbsenceCodes.Of(a) : null, Time(slot?.Arrival), Time(slot?.Departure), prepared is not null && prepared != d, name);
    }

    private static string? Time(LocalTime? t) => t is { } v ? ChildcareReader.Time.Format(v) : null;

    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static DeclaredView DeclaredOf(DeclaredFigures d)
    {
        static string? Optional(decimal? value) => value is { } v ? Amount(v) : null;
        return new DeclaredView(d.NormalHours, d.ActivityDays, d.PaidLeaveDays, Optional(d.ComplementaryHours), Optional(d.OvertimeHours),
            Optional(d.NetPay), Optional(d.Upkeep), Optional(d.Meals));
    }

    private static StatementFiguresView FiguresView(MonthFigures f, Currency currency, bool beforeCmg)
    {
        return new StatementFiguresView(
            DeclarationOf(f.Declaration),
            ChildcareMoneyView.Of(f.BasePay, currency), ChildcareMoneyView.Of(f.Deduction, currency), ChildcareMoneyView.Of(f.ExtraPay, currency),
            ChildcareMoneyView.Of(f.PaidLeavePay, currency), ChildcareMoneyView.Of(f.Cost, currency), beforeCmg);
    }

    private static DeclarationView DeclarationOf(PajemploiDeclaration d)
        => new(d.NormalHours, d.ActivityDays, d.PaidLeaveDays, Amount(d.ComplementaryHours), Amount(d.OvertimeHours), Amount(d.NetPay),
            Amount(d.Upkeep), Amount(d.Meals), Amount(d.Mileage));
}
