using System.Globalization;
using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Application;

/// <summary>
/// Issue #12: the yearly review's loans, every visible one in full (archived and ended ones included: the overview's D7 does not
/// apply), each as <see cref="LoanMath.At"/> on the period's last counted day minus on its start.
/// </summary>
public sealed class LoansReview(
    IVisibleItems visible, IItemRegistry items, ILoanStore loans, IHouseholdCalendar calendar, IHouseholdSettings household, IFxRates fx)
    : ILoansReview
{
    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    public async Task<Result<LoansReviewPart>> ReviewAsync(
        PersonId reader, VisibilityScope scope, LocalDate start, LocalDate end, CancellationToken ct)
    {
        var found = await loans.FindManyAsync(await visible.ForAsync(reader, scope, includeArchived: true, ct), ct);
        var accesses = await items.FindManyAsync(found.Select(l => l.ItemId).ToList(), ct);
        var zone = await calendar.ZoneAsync(ct);
        var reference = await household.ReferenceCurrencyAsync(ct);
        var read = found.Where(l => accesses.ContainsKey(l.ItemId)).Select(l => (Loan: l, Item: accesses[l.ItemId])).ToList();
        var foreign = read.Select(x => LoanViews.CurrencyOf(x.Item)).Where(c => c != reference).Distinct().ToList();
        // The reference's own quote is needed too: rates are per USD. Without a foreign loan the table is never looked up
        // (Convert returns a reference amount as is), so an empty one is safe.
        var rates = foreign.Count == 0 ? RateTable.Empty(end, 0) : await fx.LoadAsync([.. foreign, reference], start, end, ct);

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        decimal? Convert(decimal amount, Currency from, LocalDate on)
        {
            if (from == reference || amount == 0m) return amount;
            if (rates.TryConvert(amount, from, reference, on, projected: false, out var converted, out _))
                return Math.Round(converted, 2, MidpointRounding.AwayFromZero);
            missing.Add(from.Code); // the loan's currency, as the other parts name it
            return null;
        }

        var rows = new List<LoanReviewRow>();
        foreach (var (loan, item) in read.OrderBy(x => x.Item.DisplayName, French).ThenBy(x => x.Item.Id.Value))
        {
            // An archived loan counts strictly before its archive day, then leaves the books (balance 0, as in the net worth).
            var archivedOn = DashboardParts.ArchiveDay(item, zone) is { } day && day <= end ? day : (LocalDate?)null;
            var last = archivedOn?.PlusDays(-1) ?? end;
            if (last <= start) continue; // archived on or before the period's first day: none of it is counted
            var currency = LoanViews.CurrencyOf(item);
            var terms = LoanReading.Terms(currency, loan.Contract, loan.Fees, loan.Repayments);
            var schedule = LoanReading.Schedule(terms, loan);
            var a = LoanMath.At(terms, schedule, start);
            var b = LoanMath.At(terms, schedule, last);
            var interest = b.InterestPaid - a.InterestPaid;
            var insurance = b.InsurancePaid - a.InsurancePaid;
            var fees = b.FeesPaid - a.FeesPaid;
            var capital = b.PrincipalRepaid - a.PrincipalRepaid;
            var endBalance = archivedOn is null ? b.Balance : 0m;
            if (interest == 0m && insurance == 0m && fees == 0m && capital == 0m && a.Balance == 0m && endBalance == 0m)
                continue; // ended before the period, or not yet disbursed
            rows.Add(new LoanReviewRow(item.Id, item.DisplayName, Convert(interest, currency, end), Convert(insurance, currency, end),
                Convert(fees, currency, end), Convert(capital, currency, end), Convert(a.Balance, currency, start),
                Convert(endBalance, currency, end), archivedOn));
        }

        static decimal? Sum(IEnumerable<decimal?> amounts)
        {
            var total = 0m;
            foreach (var amount in amounts)
            {
                if (amount is not { } value) return null;
                total += value;
            }
            return total;
        }
        return Result.Success(new LoansReviewPart(Sum(rows.Select(r => r.Interest)), Sum(rows.Select(r => r.Insurance)),
            Sum(rows.Select(r => r.Fees)), Sum(rows.Select(r => r.CapitalRepaid)), Sum(rows.Select(r => r.StartBalance)),
            Sum(rows.Select(r => r.EndBalance)), rows, missing.ToList()));
    }
}
