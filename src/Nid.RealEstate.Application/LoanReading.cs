using System.Globalization;
using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Application;

/// <summary>Spec 3A2 §3: what the engine is handed, and what is read off its schedule.</summary>
internal static class LoanReading
{
    /// <summary>The stored contract, fees and repayments as the engine's terms; repayments by date, then id (spec §3).</summary>
    public static LoanTerms Terms(Currency currency, LoanContract contract, IEnumerable<StoredFee> fees, IEnumerable<StoredRepayment> repayments)
        => new(currency, contract.Principal, contract.DisbursedOn, contract.FirstPaymentDate, contract.TotalPayments, contract.Rounding,
            contract.FirstPeriod, contract.Phases.Select(p => p.Phase).ToList(), contract.Insurances.Select(i => i.Insurance).ToList(),
            fees.Select(f => f.Fee).ToList(),
            repayments.OrderBy(r => r.Repayment.OnDate).ThenBy(r => r.Id).Select(r => r.Repayment).ToList());

    /// <summary>A stored loan always computes (spec §1): every write that would break it is refused before it is stored.</summary>
    public static LoanSchedule Schedule(LoanTerms terms)
        => LoanScheduleBuilder.Build(terms).TryGetValue(out var schedule)
            ? schedule!
            : throw new InvalidOperationException("A stored loan no longer computes.");

    /// <summary>Precision 6: the later of its last row's due date and its last repayment's date.</summary>
    public static LocalDate EndOf(LoanTerms terms, LoanSchedule schedule)
    {
        LocalDate? lastRow = schedule.Rows.Count > 0 ? schedule.Rows[^1].DueDate : null;
        LocalDate? lastRepayment = terms.Events.Count > 0 ? terms.Events.Max(e => e.OnDate) : null;
        return (lastRow, lastRepayment) switch
        {
            ({ } row, { } repayment) => row > repayment ? row : repayment,
            ({ } row, null) => row,
            (null, { } repayment) => repayment,
            _ => terms.DisbursedOn,
        };
    }

    /// <summary>Spec §2: ended once its end is past.</summary>
    public static bool Ended(LoanTerms terms, LoanSchedule schedule, LocalDate today) => EndOf(terms, schedule) < today;

    /// <summary>An amount on the wire: invariant, two decimals — every stored amount has at most two, the engine counts in cents.</summary>
    public static string Amount(decimal x) => x.ToString("0.00", CultureInfo.InvariantCulture);

    public static string? Amount(decimal? x) => x is { } value ? Amount(value) : null;

    /// <summary>A rate on the wire: invariant, up to six decimals, no trailing zero (<c>0.035</c>).</summary>
    public static string Rate(decimal x) => x.ToString("0.######", CultureInfo.InvariantCulture);

    public static string? Rate(decimal? x) => x is { } value ? Rate(value) : null;
}
