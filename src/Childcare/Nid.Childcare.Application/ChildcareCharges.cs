using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Childcare.Application;

/// <summary>
/// Childcare decision 22: each validated statement is a Pajemploi debit of the month's cost, expected two days after its
/// validation (Pajemploi+ debits about two days after the declaration), and no later than the end of the month that follows
/// it: a past month entered long after was debited back then, not in the month it was typed in. A month not validated
/// expects nothing.
/// </summary>
public sealed class ChildcareCharges(IVisibleItems visible, IItemRegistry items, IChildcareStore store, IHouseholdSettings household, IHouseholdCalendar calendar)
    : IExpectedCharges
{
    /// <summary>Issue #514: a month's pay, on the arrangement's page; childcare has amounts on the calendar, never events (#241).</summary>
    public static readonly ChargeKind Childcare = new(ChargeKinds.Childcare, CalendarModules.Childcare);

    private static readonly IReadOnlyList<string> Hints = ["PAJEMPLOI", "URSSAF"];

    public async Task<Result<IReadOnlyList<ExpectedCharge>>> ChargesAsync(PersonId reader, VisibilityScope scope, LocalDate from, LocalDate to, CancellationToken ct)
    {
        var currency = await household.ReferenceCurrencyAsync(ct);
        var zone = await calendar.ZoneAsync(ct);
        // Closed ones too: the last month is debited after the end.
        var found = await store.FindManyAsync((await visible.ForAsync(reader, scope, includeArchived: true, ct)).ToList(), ct);
        var ids = found.Select(a => a.ItemId).ToList();
        var registered = await items.FindManyAsync(ids, ct);
        var statements = (await store.StatementsOfAsync(ids, ct)).ToLookup(s => s.ItemId); // batched reads spec §3
        var charges = new List<ExpectedCharge>();
        foreach (var arrangement in found)
        {
            if (!registered.TryGetValue(arrangement.ItemId, out var item))
                continue;
            foreach (var statement in statements[arrangement.ItemId])
            {
                if (statement is not { Validated: true, Figures: { } figures, ValidatedAt: { } at })
                    continue;
                var paidBy = statement.Month.PlusMonths(1).ToDateInterval().End;
                var due = LocalDate.Min(at.InZone(zone).Date.PlusDays(2), paidBy);
                if (due >= from && due <= to && Money.Create(Math.Round(figures.Cost, 2, MidpointRounding.AwayFromZero), currency).TryGetValue(out var amount))
                    charges.Add(new ExpectedCharge("childcare", arrangement.ItemId, item.DisplayName, Childcare,
                        $"statement:{StatementAssembly.Format(statement.Month)}", due, amount, false, 5, Hints));
            }
        }
        return Result.Success<IReadOnlyList<ExpectedCharge>>(charges);
    }
}

/// <summary>
/// #714: a month not validated yet is debited the month after, like a validated one: its cost estimated as the dashboard's (decision 20,
/// the last validated cost when it cannot be computed), on the day of the month the last validated one was debited (the 5th without
/// one), never before today. The calendar's only: the bank reconciliation links validated statements (<see cref="ChildcareCharges"/>).
/// </summary>
public sealed class ChildcareProjectedCharges(
    IVisibleItems visible, IChildcareStore store, StatementAssembly assembly, IHouseholdSettings household, IHouseholdCalendar calendar)
    : IProjectedCharges
{
    private const int DefaultDay = 5;

    public async Task<Result<IReadOnlyList<ExpectedCharge>>> ChargesAsync(PersonId reader, VisibilityScope scope, LocalDate from, LocalDate to, CancellationToken ct)
    {
        var currency = await household.ReferenceCurrencyAsync(ct);
        var zone = await calendar.ZoneAsync(ct);
        var today = await calendar.TodayAsync(ct);
        // Closed ones too: the last month is debited after the end.
        var found = await store.FindManyAsync((await visible.ForAsync(reader, scope, includeArchived: true, ct)).ToList(), ct);
        var charges = new List<ExpectedCharge>();
        // The months debited in the window; a statement exists up to today's month only (StatementContext.Months).
        for (var month = from.ToYearMonth().PlusMonths(-1); month.CompareTo(to.ToYearMonth().PlusMonths(-1)) <= 0; month = month.PlusMonths(1))
        {
            foreach (var context in (await assembly.ContextsAsync([.. found.Select(a => (a, month))], ct)).Values)
            {
                if (context.StoredMonth is { Validated: true })
                    continue;
                var validated = context.Stored.Where(s => s is { Validated: true, Figures: not null, ValidatedAt: not null } && s.Month <= month)
                    .OrderBy(s => s.Month).ToList();
                if (((await assembly.EstimateAsync(context, ct)).Figures?.Cost ?? (validated.Count > 0 ? validated[^1].Figures!.Cost : null)) is not { } cost)
                    continue;
                var paidIn = month.PlusMonths(1);
                var day = validated.Count > 0 ? validated[^1].ValidatedAt!.Value.InZone(zone).Date.PlusDays(2).Day : DefaultDay;
                var due = LocalDate.Max(paidIn.OnDayOfMonth(Math.Min(day, paidIn.ToDateInterval().End.Day)), today);
                if (due >= from && due <= to && Money.Create(Math.Round(cost, 2, MidpointRounding.AwayFromZero), currency).TryGetValue(out var amount))
                    charges.Add(new ExpectedCharge("childcare", context.Arrangement.ItemId, context.Item.DisplayName, ChildcareCharges.Childcare,
                        $"estimate:{StatementAssembly.Format(month)}", due, amount, false, 5, []));
            }
        }
        return Result.Success<IReadOnlyList<ExpectedCharge>>(charges);
    }
}
