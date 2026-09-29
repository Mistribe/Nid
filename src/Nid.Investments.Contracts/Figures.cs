using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Investments.Contracts;

/// <summary>
/// 2B1a spec §4, at date d. Null means unknown (no value on or before d) — never zero. <see cref="GainPct"/> is null
/// when unknown or undefined (net contributions ≤ 0): <see cref="Value"/> tells which.
/// </summary>
public sealed record EnvelopeFigures(
    Money? Value, LocalDate? AsOf,
    Money? NetContributions, Money? Gain, decimal? GainPct,
    Money? Fees, Money? GainAfterFees, Money? Income,
    Money ContributionsSince, Money WithdrawalsSince,
    bool MissingOpening, int? StaleDays);

/// <summary>2B1a spec §4 <c>period(a, b)</c>; <see cref="Performance"/> null when either value is unknown.</summary>
public sealed record PeriodPerformance(
    LocalDate From, LocalDate To, LocalDate? StartAsOf, Money? StartValue, LocalDate? EndAsOf, Money? EndValue,
    Money? NetContributions, Money? Performance);

public interface IEnvelopePerformance
{
    /// <summary>As the ambient actor; <paramref name="on"/> defaults to the household's today.</summary>
    Task<UnambitiousFx.Functional.Result<EnvelopeFigures>> GetAsync(ItemId envelope, LocalDate? on, CancellationToken ct);

    Task<UnambitiousFx.Functional.Result<PeriodPerformance>> PeriodAsync(ItemId envelope, LocalDate from, LocalDate to, CancellationToken ct);
}
