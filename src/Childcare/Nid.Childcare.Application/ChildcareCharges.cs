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
                    charges.Add(new ExpectedCharge("childcare", arrangement.ItemId, item.DisplayName, ChargeKind.Childcare,
                        $"statement:{StatementAssembly.Format(statement.Month)}", due, amount, false, 5, Hints));
            }
        }
        return Result.Success<IReadOnlyList<ExpectedCharge>>(charges);
    }
}
