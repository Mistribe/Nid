using Nid.Core.Contracts;
using NodaTime;

namespace Nid.RealEstate.Contracts;

/// <summary>Property return spec §3: the assumptions stored on a property; a null field takes its default.</summary>
public sealed record ReturnAssumptions(
    int? YearsAfterPinel = null, decimal? ResalePrice = null, decimal? ValueGrowth = null, decimal? RentGrowth = null,
    decimal? ChargesGrowth = null, int? VacancyMonths = null, decimal? RentAfterPinel = null, decimal? MarginalRate = null,
    decimal? ResaleFees = null);

/// <summary>Spec §3: the assumptions the calculation uses; <c>Defaulted</c> names the fields that took their default.</summary>
public sealed record EffectiveAssumptions(
    int YearsAfterPinel, decimal? ResalePrice, decimal ValueGrowth, decimal RentGrowth, decimal ChargesGrowth, int VacancyMonths,
    decimal? RentAfterPinel, decimal MarginalRate, decimal ResaleFees, IReadOnlySet<string> Defaulted);

/// <summary>A linked loan as the return reads it; <c>Rate</c> converts its amounts to the property's currency.</summary>
public sealed record ReturnLoan(ItemId Id, LoanTerms Terms, LoanSchedule Schedule, decimal Rate);

/// <summary>Spec §4: everything the return reads. <c>CapitalGainsTax</c> takes the net sale price and the sale date.</summary>
public sealed record ReturnInputs(
    PropertyDetails Property, IReadOnlyList<Flow> Flows, IReadOnlyList<Tenancy> Tenancies, Valuation? Latest,
    IReadOnlyList<ReturnLoan> Loans, EffectiveAssumptions Assumptions, decimal RentalSocialRate, PinelOutlook? Pinel,
    Func<decimal, LocalDate, decimal> CapitalGainsTax, LocalDate Today);

/// <summary>
/// Spec §4.2: one dated cash flow, negative when the household pays. <c>Line</c>: purchase, loan_in, loan_fees, rent, charges, works,
/// loan_payment, rental_tax, pinel, resale, resale_fees, loan_balance, penalty, capital_gains_tax, pinel_clawback.
/// </summary>
public sealed record CashFlow(LocalDate On, string Line, decimal Amount);

/// <summary>Spec §4.5: a resale's result; <c>IrrPercent</c> at 0.1, null when not meaningful.</summary>
public sealed record ReturnOutcome(LocalDate ResaleOn, decimal ResalePrice, decimal? IrrPercent, decimal NetGain, bool PinelClawback);

/// <summary>Spec §4.5: a line's total, until today and after.</summary>
public sealed record ReturnBreakdownLine(string Line, decimal Realized, decimal Projected);

/// <summary>Spec §4.5: the household's scenario; <c>PriceSource</c> is <c>entered</c> or <c>growth</c>.</summary>
public sealed record ReturnScenario(ReturnOutcome Outcome, string PriceSource, decimal SavingsEffort, IReadOnlyList<ReturnBreakdownLine> Breakdown);

/// <summary>Spec §4.5–§4.6: the scenario (null without a price), a resale each year from today to +15, the Pinel end, the warnings.</summary>
public sealed record PropertyReturnResult(ReturnScenario? Scenario, IReadOnlyList<ReturnOutcome> Sensitivity, LocalDate? PinelEnd, IReadOnlyList<string> Warnings);
