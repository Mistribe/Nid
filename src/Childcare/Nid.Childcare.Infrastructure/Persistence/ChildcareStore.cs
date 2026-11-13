using Microsoft.EntityFrameworkCore;
using Nid.Childcare.Application;
using Nid.Childcare.Contracts;
using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Childcare.Infrastructure.Persistence;

public sealed class ChildcareStore(ChildcareDbContext db, IClock clock) : IChildcareStore
{
    public async Task CreateAsync(Arrangement arrangement, Terms terms, CancellationToken ct, ImportMark? import = null)
    {
        var row = new ArrangementRow { ItemId = arrangement.ItemId.Value };
        Apply(row, arrangement);
        Mark(row, import);
        db.Arrangements.Add(row);
        AddTerms(terms);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Arrangement?> FindAsync(ItemId id, CancellationToken ct)
        => await db.Arrangements.AsNoTracking().SingleOrDefaultAsync(a => a.ItemId == id.Value, ct) is { } row ? ToArrangement(row) : null;

    public async Task<IReadOnlyList<Arrangement>> FindManyAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct)
    {
        var wanted = ids.Select(i => i.Value).ToList();
        return (await db.Arrangements.AsNoTracking().Where(a => wanted.Contains(a.ItemId)).ToListAsync(ct)).Select(ToArrangement).ToList();
    }

    public async Task<Result> UpdateAsync(Arrangement arrangement, CancellationToken ct)
    {
        var row = await db.Arrangements.SingleOrDefaultAsync(a => a.ItemId == arrangement.ItemId.Value, ct);
        if (row is null)
            return Result.Failure(new NotFoundFailure("childcare", arrangement.ItemId.ToString()));

        Apply(row, arrangement);
        db.Entry(row).Property(r => r.Version).OriginalValue = arrangement.Version;
        // Always written: a terms-only edit still bumps xmin, so a later stale edit meets the conflict.
        db.Entry(row).Property(r => r.CarerName).IsModified = true;
        try
        {
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(new Failure(ChildcareCodes.Conflict, "The arrangement changed since it was read.", null));
        }
    }

    public async Task<IReadOnlyList<Terms>> TermsOfAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct)
    {
        var wanted = ids.Select(i => i.Value).ToList();
        var rows = await db.Terms.AsNoTracking().Where(t => wanted.Contains(t.ItemId)).OrderBy(t => t.ItemId).ThenBy(t => t.From).ToListAsync(ct);
        var days = (await db.Weekdays.AsNoTracking().Where(w => wanted.Contains(w.ItemId)).ToListAsync(ct))
            .ToLookup(w => (w.ItemId, w.TermsFrom, w.WeekType));
        var types = (await db.WeekTypes.AsNoTracking().Where(w => wanted.Contains(w.ItemId)).OrderBy(w => w.Rank).ToListAsync(ct))
            .ToLookup(w => (w.ItemId, w.TermsFrom));
        return rows.Select(r => new Terms(new ItemId(r.ItemId), r.From, r.To, r.NetHourlyRate, r.GrossHourlyRate, r.OvertimeRate, r.UpkeepPer9h,
            r.LunchPrice,
            types[(r.ItemId, r.From)].Select(w => new WeekType(w.Name, w.Weeks,
                days[(r.ItemId, r.From, w.Rank)].OrderBy(d => d.Day).Select(d => new WeekdaySlot((IsoDayOfWeek)d.Day, d.Arrival, d.Departure, d.Lunch, d.Snack)).ToList())).ToList(),
            r.SnackPrice, MealProviderCodes.Parse(r.LunchProvider), MealProviderCodes.Parse(r.SnackProvider), r.UnpaidAbsences.Select(AbsenceCodes.Parse).ToList())).ToList();
    }

    public async Task ReplaceTermsAsync(Terms terms, CancellationToken ct)
    {
        var row = await db.Terms.SingleAsync(t => t.ItemId == terms.ItemId.Value && t.From == terms.From, ct);
        Apply(row, terms);
        db.WeekTypes.RemoveRange(await db.WeekTypes.Where(w => w.ItemId == terms.ItemId.Value && w.TermsFrom == terms.From).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        AddWeekTypes(terms);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddTermsAsync(Terms terms, CancellationToken ct)
    {
        var current = await db.Terms.Where(t => t.ItemId == terms.ItemId.Value).OrderByDescending(t => t.From).FirstAsync(ct);
        current.To = terms.From.PlusDays(-1);
        AddTerms(terms);
        await TouchAsync(terms.ItemId, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetUnpaidAbsencesAsync(ItemId id, LocalDate from, IReadOnlyList<AbsenceReason> reasons, CancellationToken ct)
    {
        var row = await db.Terms.SingleAsync(t => t.ItemId == id.Value && t.From == from, ct);
        row.UnpaidAbsences = [.. reasons.Select(AbsenceCodes.Of)];
        await TouchAsync(id, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<(ItemId Item, CareGap Gap)>> GapsOfAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct)
    {
        var wanted = ids.Select(i => i.Value).ToList();
        var rows = await db.Gaps.AsNoTracking().Where(g => wanted.Contains(g.ItemId)).OrderBy(g => g.ItemId).ThenBy(g => g.Monday).ToListAsync(ct);
        return rows.Select(r => (new ItemId(r.ItemId), new CareGap(r.Monday, r.Weeks))).ToList();
    }

    public async Task ReplaceGapsAsync(ItemId id, IReadOnlyList<CareGap> gaps, CancellationToken ct)
    {
        db.Gaps.RemoveRange(await db.Gaps.Where(g => g.ItemId == id.Value).ToListAsync(ct));
        db.Gaps.AddRange(gaps.Select(g => new GapRow { ItemId = id.Value, Monday = g.Monday, Weeks = g.Weeks }));
        await TouchAsync(id, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<(ItemId Item, WeekOverride Override)>> OverridesOfAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct)
    {
        var wanted = ids.Select(i => i.Value).ToList();
        var rows = await db.WeekOverrides.AsNoTracking().Where(o => wanted.Contains(o.ItemId)).OrderBy(o => o.ItemId).ThenBy(o => o.Monday).ToListAsync(ct);
        return rows.Select(r => (new ItemId(r.ItemId), new WeekOverride(r.Monday, r.WeekType))).ToList();
    }

    public async Task SetOverrideAsync(ItemId id, LocalDate monday, int? weekType, CancellationToken ct)
    {
        var row = await db.WeekOverrides.SingleOrDefaultAsync(o => o.ItemId == id.Value && o.Monday == monday, ct);
        if (weekType is not { } rank)
        {
            if (row is null)
                return;
            db.WeekOverrides.Remove(row);
        }
        else if (row is null)
            db.WeekOverrides.Add(new WeekOverrideRow { ItemId = id.Value, Monday = monday, WeekType = (short)rank });
        else
            row.WeekType = (short)rank;
        await TouchAsync(id, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetEndAsync(ItemId id, LocalDate? to, CancellationToken ct)
    {
        var current = await db.Terms.Where(t => t.ItemId == id.Value).OrderByDescending(t => t.From).FirstAsync(ct);
        current.To = to;
        await TouchAsync(id, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Statement>> StatementsOfAsync(ItemId id, CancellationToken ct)
        => (await db.Statements.AsNoTracking().Where(st => st.ItemId == id.Value).OrderBy(st => st.Month).ToListAsync(ct))
            .Select(r => ToStatement(r, [])).ToList();

    public async Task<Statement?> FindStatementAsync(ItemId id, YearMonth month, CancellationToken ct)
    {
        var first = month.OnDayOfMonth(1);
        var row = await db.Statements.AsNoTracking().SingleOrDefaultAsync(st => st.ItemId == id.Value && st.Month == first, ct);
        if (row is null)
            return null;
        var days = await db.StatementDays.AsNoTracking().Where(d => d.ItemId == id.Value && d.Month == first).OrderBy(d => d.Date).ToListAsync(ct);
        return ToStatement(row, days.Select(ToDay).ToList());
    }

    public async Task<Result> SaveDraftAsync(Statement draft, CancellationToken ct)
    {
        var first = draft.Month.OnDayOfMonth(1);
        var row = await db.Statements.SingleOrDefaultAsync(st => st.ItemId == draft.ItemId.Value && st.Month == first, ct);
        if (row is null)
        {
            if (draft.Version != 0)
                return StatementConflict();
            row = new StatementRow { ItemId = draft.ItemId.Value, Month = first };
            db.Statements.Add(row);
        }
        else
        {
            if (draft.Version == 0 || row.Version != draft.Version)
                return StatementConflict();
            if (row.Validated)
                return Result.Failure(new Failure(ChildcareCodes.StatementValidated, "A validated statement is read only.", null));
            db.Entry(row).Property(r => r.Version).OriginalValue = draft.Version;
            db.Entry(row).Property(r => r.Mileage).IsModified = true;
        }

        row.Mileage = draft.Mileage;
        row.Cmg = draft.Cmg;
        row.Contributions = draft.Contributions;
        row.Exemption = draft.Exemption;
        Declare(row, draft.Declared);
        ReplaceDays(draft.ItemId, first, draft.Days, await db.StatementDays.Where(d => d.ItemId == draft.ItemId.Value && d.Month == first).ToListAsync(ct));
        return await SaveStatementAsync(ct);
    }

    public async Task<Result> ValidateAsync(ItemId id, YearMonth month, MonthFigures figures, IReadOnlyList<StatementDay> days, uint version, CancellationToken ct)
    {
        var first = month.OnDayOfMonth(1);
        var row = await db.Statements.SingleOrDefaultAsync(st => st.ItemId == id.Value && st.Month == first, ct);
        if (row is null || row.Version != version)
            return StatementConflict();
        if (row.Validated)
            return Result.Failure(new Failure(ChildcareCodes.StatementValidated, "The statement is already validated.", null));
        db.Entry(row).Property(r => r.Version).OriginalValue = version;
        var d = figures.Declaration;
        row.Validated = true;
        row.ValidatedAt = clock.GetCurrentInstant();
        row.NormalHours = d.NormalHours;
        row.ActivityDays = d.ActivityDays;
        row.PaidLeaveDays = d.PaidLeaveDays;
        row.ComplementaryHours = d.ComplementaryHours;
        row.OvertimeHours = d.OvertimeHours;
        row.NetPay = d.NetPay;
        row.Upkeep = d.Upkeep;
        row.Meals = d.Meals;
        row.Mileage = d.Mileage;
        row.BasePay = figures.BasePay;
        row.Deduction = figures.Deduction;
        row.ExtraPay = figures.ExtraPay;
        row.PaidLeavePay = figures.PaidLeavePay;
        row.Cmg = figures.Cmg;
        row.Contributions = figures.Contributions;
        row.Exemption = figures.Exemption;
        row.Cost = figures.Cost;
        row.TaxBase = figures.TaxBase;
        row.ChildSickDeducted = figures.ChildSickDeducted;
        ReplaceDays(id, first, days, await db.StatementDays.Where(d => d.ItemId == id.Value && d.Month == first).ToListAsync(ct));
        return await SaveStatementAsync(ct);
    }

    public async Task ReopenAsync(ItemId id, YearMonth month, CancellationToken ct)
    {
        var first = month.OnDayOfMonth(1);
        var row = await db.Statements.SingleAsync(st => st.ItemId == id.Value && st.Month == first, ct);
        row.Validated = false;
        row.ValidatedAt = null;
        row.NormalHours = row.ActivityDays = row.PaidLeaveDays = row.ChildSickDeducted = null;
        row.ComplementaryHours = row.OvertimeHours = row.NetPay = row.Upkeep = row.Meals = null;
        row.BasePay = row.Deduction = row.ExtraPay = row.PaidLeavePay = row.Cost = row.TaxBase = null;
        await db.SaveChangesAsync(ct);
    }

    public async Task DiscardAsync(ItemId id, YearMonth month, CancellationToken ct)
    {
        var first = month.OnDayOfMonth(1);
        if (await db.Statements.SingleOrDefaultAsync(st => st.ItemId == id.Value && st.Month == first && !st.Validated, ct) is not { } row)
            return;
        db.StatementDays.RemoveRange(await db.StatementDays.Where(d => d.ItemId == id.Value && d.Month == first).ToListAsync(ct));
        db.Statements.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    private void ReplaceDays(ItemId id, LocalDate month, IReadOnlyList<StatementDay> days, List<StatementDayRow> stored)
    {
        db.StatementDays.RemoveRange(stored);
        db.StatementDays.AddRange(days.Select(d => new StatementDayRow
        {
            ItemId = id.Value, Month = month, Date = d.Date, Planned = d.Planned, Present = d.Present, Arrival = d.Arrival,
            Departure = d.Departure, Lunch = d.Lunch, Snack = d.Snack, Absence = d.Absence is { } a ? AbsenceCodes.Of(a) : null,
        }));
    }

    private async Task<Result> SaveStatementAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (DbUpdateException)
        {
            // A stale xmin, or a concurrent first save of the same month (primary key).
            db.ChangeTracker.Clear();
            return StatementConflict();
        }
    }

    private static Result StatementConflict() => Result.Failure(new Failure(ChildcareCodes.StatementConflict, "The statement changed since it was read.", null));

    private static Statement ToStatement(StatementRow r, IReadOnlyList<StatementDay> days)
        => new(new ItemId(r.ItemId), r.Month.ToYearMonth(), r.Validated, r.Mileage, r.Cmg, days,
            r.Validated
                ? new MonthFigures(
                    new PajemploiDeclaration(r.NormalHours!.Value, r.ActivityDays!.Value, r.PaidLeaveDays, r.ComplementaryHours!.Value, r.OvertimeHours!.Value,
                        r.NetPay!.Value, r.Upkeep!.Value, r.Meals!.Value, r.Mileage),
                    r.BasePay!.Value, r.Deduction!.Value, r.ExtraPay!.Value, r.PaidLeavePay!.Value, r.Cmg!.Value, r.Cost!.Value, r.TaxBase!.Value,
                    r.ChildSickDeducted!.Value, r.Contributions ?? 0m, r.Exemption ?? 0m)
                : null,
            r.Version, r.ValidatedAt,
            new DeclaredFigures(r.DeclaredNetPay, r.DeclaredUpkeep, r.DeclaredMeals, r.DeclaredNormalHours, r.DeclaredActivityDays, r.DeclaredPaidLeaveDays,
                r.DeclaredComplementaryHours, r.DeclaredOvertimeHours), r.Contributions, r.Exemption);

    private static void Declare(StatementRow row, DeclaredFigures? d)
    {
        (row.DeclaredNetPay, row.DeclaredUpkeep, row.DeclaredMeals) = (d?.NetPay, d?.Upkeep, d?.Meals);
        (row.DeclaredNormalHours, row.DeclaredActivityDays, row.DeclaredPaidLeaveDays) = (d?.NormalHours, d?.ActivityDays, d?.PaidLeaveDays);
        (row.DeclaredComplementaryHours, row.DeclaredOvertimeHours) = (d?.ComplementaryHours, d?.OvertimeHours);
    }

    private static StatementDay ToDay(StatementDayRow d)
        => new(d.Date, d.Planned, d.Present, d.Arrival, d.Departure, d.Lunch, d.Absence is { } a ? AbsenceCodes.Parse(a) : null, d.Snack);

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ImportedArrangement>>> FindImportedAsync(IReadOnlyCollection<string> refs, CancellationToken ct)
    {
        var wanted = refs.Distinct(StringComparer.Ordinal).ToList();
        var rows = await db.Arrangements.AsNoTracking().Where(r => r.ImportRef != null && wanted.Contains(r.ImportRef))
            .Select(r => new { r.ItemId, r.ImportRef, r.ImportHash, r.ImportedBy }).ToListAsync(ct);
        return rows.GroupBy(r => r.ImportRef!, StringComparer.Ordinal).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<ImportedArrangement>)g.Select(r => new ImportedArrangement(new ItemId(r.ItemId), r.ImportHash!.Trim(), new PersonId(r.ImportedBy!.Value))).ToList(),
            StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<ItemId, ImportMark>> ImportMarksAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct)
    {
        var wanted = ids.Select(i => i.Value).ToList();
        return (await db.Arrangements.AsNoTracking().Where(r => wanted.Contains(r.ItemId) && r.ImportRef != null).ToListAsync(ct))
            .ToDictionary(r => new ItemId(r.ItemId), r => new ImportMark(r.ImportRef!, r.ImportHash!.Trim(), new PersonId(r.ImportedBy!.Value)));
    }

    public async Task ReplaceImportedAsync(Arrangement arrangement, IReadOnlyList<Terms> revisions, IReadOnlyList<CareGap> gaps, IReadOnlyList<WeekOverride> overrides, ImportMark import, CancellationToken ct)
    {
        var id = arrangement.ItemId.Value;
        var row = await db.Arrangements.SingleAsync(a => a.ItemId == id, ct);
        Apply(row, arrangement);
        Mark(row, import);
        db.WeekOverrides.RemoveRange(await db.WeekOverrides.Where(o => o.ItemId == id).ToListAsync(ct));
        db.Terms.RemoveRange(await db.Terms.Where(t => t.ItemId == id).ToListAsync(ct));
        db.Gaps.RemoveRange(await db.Gaps.Where(g => g.ItemId == id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        foreach (var terms in revisions)
            AddTerms(terms);
        db.Gaps.AddRange(gaps.Select(g => new GapRow { ItemId = id, Monday = g.Monday, Weeks = g.Weeks }));
        db.WeekOverrides.AddRange(overrides.Select(o => new WeekOverrideRow { ItemId = id, Monday = o.Monday, WeekType = (short)o.WeekType }));
        await db.SaveChangesAsync(ct);
    }

    public async Task PutValidatedAsync(Statement statement, CancellationToken ct)
    {
        var first = statement.Month.OnDayOfMonth(1);
        var row = await db.Statements.SingleOrDefaultAsync(st => st.ItemId == statement.ItemId.Value && st.Month == first, ct);
        if (row is null)
        {
            row = new StatementRow { ItemId = statement.ItemId.Value, Month = first };
            db.Statements.Add(row);
        }
        var figures = statement.Figures!;
        var d = figures.Declaration;
        (row.Validated, row.ValidatedAt, row.Mileage, row.Cmg, row.Contributions, row.Exemption) = (true, statement.ValidatedAt, d.Mileage, figures.Cmg, figures.Contributions, figures.Exemption);
        Declare(row, statement.Declared);
        (row.NormalHours, row.ActivityDays, row.PaidLeaveDays, row.ComplementaryHours, row.OvertimeHours) =
            (d.NormalHours, d.ActivityDays, d.PaidLeaveDays, d.ComplementaryHours, d.OvertimeHours);
        (row.NetPay, row.Upkeep, row.Meals, row.BasePay, row.Deduction, row.ExtraPay) =
            (d.NetPay, d.Upkeep, d.Meals, figures.BasePay, figures.Deduction, figures.ExtraPay);
        (row.PaidLeavePay, row.Cost, row.TaxBase, row.ChildSickDeducted) = (figures.PaidLeavePay, figures.Cost, figures.TaxBase, figures.ChildSickDeducted);
        ReplaceDays(statement.ItemId, first, statement.Days, await db.StatementDays.Where(x => x.ItemId == statement.ItemId.Value && x.Month == first).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }

    private static void Mark(ArrangementRow row, ImportMark? import)
    {
        if (import is null)
            return;
        (row.ImportRef, row.ImportHash, row.ImportedBy) = (import.Ref, import.Hash, import.ImportedBy.Value);
    }

    /// <summary>A write to the terms or the gaps rewrites the arrangement row too, so its xmin moves and an edit read before it conflicts.</summary>
    private async Task TouchAsync(ItemId id, CancellationToken ct)
        => db.Entry(await db.Arrangements.SingleAsync(a => a.ItemId == id.Value, ct)).Property(r => r.CarerName).IsModified = true;

    private void AddTerms(Terms terms)
    {
        var row = new TermsRow { ItemId = terms.ItemId.Value, From = terms.From, To = terms.To };
        Apply(row, terms);
        db.Terms.Add(row);
        AddWeekTypes(terms);
    }

    private void AddWeekTypes(Terms terms)
    {
        foreach (var (type, rank) in terms.WeekTypes.Select((t, i) => (t, (short)i)))
        {
            db.WeekTypes.Add(new WeekTypeRow { ItemId = terms.ItemId.Value, TermsFrom = terms.From, Rank = rank, Name = type.Name, Weeks = type.Weeks });
            db.Weekdays.AddRange(type.Schedule.Select(s => new WeekdayRow
            {
                ItemId = terms.ItemId.Value, TermsFrom = terms.From, WeekType = rank, Day = (short)s.Day, Arrival = s.Arrival, Departure = s.Departure, Lunch = s.Lunch, Snack = s.Snack,
            }));
        }
    }

    private static void Apply(ArrangementRow row, Arrangement a)
    {
        row.ChildId = a.Child.Value;
        row.CarerName = a.CarerName;
        row.CarerPajemploi = a.CarerPajemploi;
        row.CarerApproval = a.CarerApproval;
    }

    private static void Apply(TermsRow row, Terms t)
    {
        row.NetHourlyRate = t.NetHourlyRate;
        row.GrossHourlyRate = t.GrossHourlyRate;
        row.OvertimeRate = t.OvertimeRate;
        row.UpkeepPer9h = t.UpkeepPer9h;
        row.LunchPrice = t.LunchPrice;
        row.SnackPrice = t.SnackPrice;
        row.LunchProvider = MealProviderCodes.Of(t.LunchProvider);
        row.SnackProvider = MealProviderCodes.Of(t.SnackProvider);
        row.UnpaidAbsences = [.. (t.UnpaidAbsences ?? AbsenceCodes.DefaultUnpaid).Select(AbsenceCodes.Of)];
    }

    private static Arrangement ToArrangement(ArrangementRow r)
        => new(new ItemId(r.ItemId), new PersonId(r.ChildId), r.CarerName, r.CarerPajemploi, r.CarerApproval, r.Version);
}
