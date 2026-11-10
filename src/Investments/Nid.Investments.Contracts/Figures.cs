using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Investments.Contracts;

/// <summary>
/// 2B1a spec §4, at date d. Null means unknown (no value on or before d) — never zero. <see cref="GainPct"/> is the gain
/// over the total paid in (opening and contributions), null when unknown or undefined (nothing paid in): <see cref="Value"/>
/// tells which. <see cref="AnnualReturnPct"/> is the XIRR in percent, null when not meaningful (data model §6).
/// </summary>
public sealed record EnvelopeFigures(
    Money? Value, LocalDate? AsOf,
    Money? NetContributions, Money? Gain, decimal? GainPct, decimal? AnnualReturnPct,
    Money? Fees, Money? GainAfterFees, Money? Income,
    Money ContributionsSince, Money WithdrawalsSince,
    bool MissingOpening, int? StaleDays);

/// <summary>
/// 2B1a spec §4 <c>period(a, b)</c>; <see cref="Performance"/> null when either value is unknown. <see cref="NoStartValue"/>: no value at
/// the start but flows before it, the start counted at the amount paid in.
/// </summary>
public sealed record PeriodPerformance(
    LocalDate From, LocalDate To, LocalDate? StartAsOf, Money? StartValue, LocalDate? EndAsOf, Money? EndValue,
    Money? NetContributions, Money? Performance, bool NoStartValue = false);

/// <summary>One point of an envelope's value series: a valuation, or the amount paid in before the first one (<c>PaidIn</c>), and the net contributions dated on or before it.</summary>
public sealed record SeriesPointFigures(LocalDate AsOf, Money Value, Money NetContributions, bool PaidIn = false);

public interface IEnvelopePerformance
{
    /// <summary>As the ambient actor; <paramref name="on"/> defaults to the household's today.</summary>
    Task<UnambitiousFx.Functional.Result<EnvelopeFigures>> GetAsync(ItemId envelope, LocalDate? on, CancellationToken ct);

    Task<UnambitiousFx.Functional.Result<PeriodPerformance>> PeriodAsync(ItemId envelope, LocalDate from, LocalDate to, CancellationToken ct);
}
