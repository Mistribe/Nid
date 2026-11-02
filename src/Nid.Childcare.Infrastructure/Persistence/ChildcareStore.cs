using Microsoft.EntityFrameworkCore;
using Nid.Childcare.Application;
using Nid.Childcare.Contracts;
using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Childcare.Infrastructure.Persistence;

public sealed class ChildcareStore(ChildcareDbContext db) : IChildcareStore
{
    public async Task CreateAsync(Arrangement arrangement, Terms terms, CancellationToken ct)
    {
        var row = new ArrangementRow { ItemId = arrangement.ItemId.Value };
        Apply(row, arrangement);
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
            .ToLookup(w => (w.ItemId, w.TermsFrom));
        return rows.Select(r => new Terms(new ItemId(r.ItemId), r.From, r.To, r.NetHourlyRate, r.GrossHourlyRate, r.OvertimeRate, r.UpkeepPer9h,
            r.MealPrice, r.ProgrammedWeeks,
            days[(r.ItemId, r.From)].OrderBy(w => w.Day).Select(w => new WeekdaySlot((IsoDayOfWeek)w.Day, w.Arrival, w.Departure, w.Meal)).ToList())).ToList();
    }

    public async Task ReplaceCurrentTermsAsync(Terms terms, CancellationToken ct)
    {
        var row = await db.Terms.SingleAsync(t => t.ItemId == terms.ItemId.Value && t.From == terms.From, ct);
        Apply(row, terms);
        db.Weekdays.RemoveRange(await db.Weekdays.Where(w => w.ItemId == terms.ItemId.Value && w.TermsFrom == terms.From).ToListAsync(ct));
        AddSchedule(terms);
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

    public async Task SetEndAsync(ItemId id, LocalDate? to, CancellationToken ct)
    {
        var current = await db.Terms.Where(t => t.ItemId == id.Value).OrderByDescending(t => t.From).FirstAsync(ct);
        current.To = to;
        await TouchAsync(id, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A write to the terms or the gaps rewrites the arrangement row too, so its xmin moves and an edit read before it conflicts.</summary>
    private async Task TouchAsync(ItemId id, CancellationToken ct)
        => db.Entry(await db.Arrangements.SingleAsync(a => a.ItemId == id.Value, ct)).Property(r => r.CarerName).IsModified = true;

    private void AddTerms(Terms terms)
    {
        var row = new TermsRow { ItemId = terms.ItemId.Value, From = terms.From, To = terms.To };
        Apply(row, terms);
        db.Terms.Add(row);
        AddSchedule(terms);
    }

    private void AddSchedule(Terms terms)
        => db.Weekdays.AddRange(terms.Schedule.Select(s => new WeekdayRow
        {
            ItemId = terms.ItemId.Value, TermsFrom = terms.From, Day = (short)s.Day, Arrival = s.Arrival, Departure = s.Departure, Meal = s.Meal,
        }));

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
        row.MealPrice = t.MealPrice;
        row.ProgrammedWeeks = t.ProgrammedWeeks;
    }

    private static Arrangement ToArrangement(ArrangementRow r)
        => new(new ItemId(r.ItemId), new PersonId(r.ChildId), r.CarerName, r.CarerPajemploi, r.CarerApproval, r.Version);
}
