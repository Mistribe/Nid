using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using NodaTime;

namespace Nid.RealEstate.Domain;

/// <summary>
/// Issue #406: a negative net value, since when, and the first day the linked loans' balance falls below the last estimate held
/// flat for good (« à valeur constante »); <c>PositiveOn</c> is null when it never does before the loans end.
/// </summary>
public sealed record NetValueTurn(LocalDate NegativeSince, LocalDate? PositiveOn);

/// <summary>A linked loan as the turn reads it: its terms, its schedule, and its rate to the property's currency.</summary>
public sealed record TurnLoan(LoanTerms Terms, LoanSchedule Schedule, decimal Rate);

public static class NetValueTurns
{
    /// <summary>
    /// Null unless the net value is negative on <paramref name="today"/>. The value at a date is the last estimate on or before
    /// it, the purchase price before the first (as the overview's curve); the balance is <see cref="LoanMath.At"/>'s, so the net
    /// value only changes on an estimate, a disbursement, a payment or a repayment: those days are the ones read.
    /// </summary>
    public static NetValueTurn? Of(PropertyDetails property, IReadOnlyList<Valuation> values, IReadOnlyList<TurnLoan> loans, LocalDate today)
    {
        var estimates = values.OrderBy(v => v.AsOf).ToList();
        decimal Balance(LocalDate d) => loans.Sum(l => LoanMath.At(l.Terms, l.Schedule, d).Balance * l.Rate);
        decimal? Net(LocalDate d)
        {
            var value = estimates.LastOrDefault(v => v.AsOf <= d)?.Value.Amount ?? (d >= property.PurchaseDate ? property.PurchasePrice : null);
            return value - Balance(d);
        }
        if (estimates.LastOrDefault(v => v.AsOf <= today) is not { } last || !(Net(today) < 0m))
            return null;

        var days = estimates.Select(v => v.AsOf).Append(property.PurchaseDate)
            .Concat(loans.SelectMany(l => l.Schedule.Rows.Select(r => r.DueDate).Concat(l.Terms.Events.Select(e => e.OnDate)).Append(l.Terms.DisbursedOn)))
            .Distinct().Order().ToList();

        // The net value holds from a change day to the next: the run goes back to the earliest change day of negative ones in a row.
        LocalDate? since = null;
        foreach (var d in days.Where(d => d <= today).Reverse())
        {
            if (!(Net(d) < 0m))
                break;
            since = d;
        }

        LocalDate? positive = null;
        foreach (var d in days.Where(d => d > today))
            positive = last.Value.Amount - Balance(d) > 0m ? positive ?? d : null;
        return new NetValueTurn(since ?? today, positive);
    }
}
