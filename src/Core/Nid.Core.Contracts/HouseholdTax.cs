using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

/// <summary>
/// #512: what Core's own screens read of the tax module (<c>Nid.Tax</c>), which Core cannot reference: the year review's figures, the
/// purchase scenario's rental tax, the sale scenario's tax page, the PER deadline and tax calendar of « À venir », and whether a person
/// declares a notice. Declared here as <c>ILoanSimulator</c> is, for a single provider (#513).
/// </summary>
public interface IHouseholdTax
{
    /// <summary>
    /// Revenue year <paramref name="year"/>'s estimate as the tax page computes it on <paramref name="today"/>, kept for the request;
    /// <see cref="TaxYearFigures.None"/> before the first year of the rules.
    /// </summary>
    Task<Result<TaxYearFigures>> YearAsync(int year, VisibilityScope scope, LocalDate today, CancellationToken ct);

    /// <summary>
    /// Property purchase scenario spec §4.1: the monthly change of the total tax when this year's estimate also counts a rental's first
    /// full year; a failure when the year cannot be estimated.
    /// </summary>
    Task<Result<decimal>> RentalPurchaseAsync(VisibilityScope scope, LocalDate today, RentalPurchaseTax purchase, CancellationToken ct);

    /// <summary>
    /// Issue #416: <paramref name="property"/>'s sale tax page as if sold on <paramref name="on"/> for <paramref name="netPrice"/> (the
    /// seller's agency fees taken off); not found when the reader does not see it.
    /// </summary>
    Task<Result<SaleItemView>> PropertySaleAsync(PersonId reader, ItemId property, LocalDate today, LocalDate on, decimal netPrice, CancellationToken ct);

    /// <summary>Tax-per spec §4.6: the PER ceiling left and the part lost on 31 December, when that day falls in [<paramref name="today"/>, <paramref name="until"/>].</summary>
    Task<Result<IReadOnlyList<DashboardEventView>>> PerDeadlineAsync(PersonId reader, VisibilityScope scope, LocalDate today, LocalDate until, CancellationToken ct);

    /// <summary>Issue #409: the household's dates of the yearly tax calendar in [<paramref name="today"/>, <paramref name="until"/>].</summary>
    Task<IReadOnlyList<DashboardEventView>> CalendarAsync(PersonId reader, VisibilityScope scope, LocalDate today, LocalDate until, CancellationToken ct);

    /// <summary>Whether <paramref name="person"/> is the only declarant of a notice: a private record, like a private item.</summary>
    Task<bool> DeclaresAloneAsync(PersonId person, CancellationToken ct);

    /// <summary>Whether <paramref name="person"/> declares any notice, which keeps a pending adult from being removed.</summary>
    Task<bool> DeclaresAsync(PersonId person, CancellationToken ct);
}

/// <summary>
/// A year's estimate, in euros: <c>HasData</c> when the year has a tax line or a profile stored for that very year (an earlier one
/// standing in alone is not).
/// </summary>
public sealed record TaxYearFigures(bool HasData, decimal TotalTax, decimal IncomeTax, decimal SocialLevies, decimal CapitalFlatTax)
{
    /// <summary>A year the rules do not cover: nothing to estimate from, nothing to pay.</summary>
    public static readonly TaxYearFigures None = new(false, 0m, 0m, 0m, 0m);
}

/// <summary>
/// A rental bought this year, over its first full year: <c>RentalRegime</c> on the wire (<c>micro</c>, <c>real</c>, null: the
/// estimate's), the rents received, and under <c>real</c> the charges and the loans' interest and insurance it deducts.
/// </summary>
public sealed record RentalPurchaseTax(string? RentalRegime, decimal Rents, decimal Charges, decimal LoanInterest);

/// <summary>
/// Property return spec §5: a property's Pinel commitment and its reduction for every year of it. Here rather than in the tax module's
/// contracts: RealEstate's contracts and domain carry it, and they read no other module's.
/// </summary>
public sealed record PinelOutlook(int StartYear, int InitialYears, int TotalYears, IReadOnlyDictionary<int, decimal> Reductions);

/// <summary>
/// The commitments part estimated from the year's tax (<see cref="CommitmentSources.Tax"/>): it holds no cash, so the annual review
/// reads its estimate once through <see cref="IHouseholdTax.YearAsync"/> rather than month by month.
/// </summary>
public interface IEstimatedCommitments : ICommitmentsContribution;

/// <summary>
/// One item sold today. <c>Status</c>: <c>taxed</c>, <c>exempt</c>, <c>loss</c>, <c>not_taxable</c>, <c>not_estimated</c>, or
/// <c>finding</c> with its <c>Finding</c> (<c>opening_date_unknown</c>, <c>other_currency</c>, <c>no_value</c>) and no figure.
/// <c>Surtax</c> is a property's; <c>Net</c> is null when not estimated.
/// </summary>
public sealed record SaleItemView(
    ItemId Item, string Module, string Name, string Kind, string Status, string? Finding, string? Value, LocalDate? ValueOn, bool Estimated,
    bool Stale, bool Approximate, int? YearsHeld, string? Gain, string? IncomeTax, string? SocialTax, string? Surtax, string? Net,
    PropertySaleView? Property, EnvelopeSaleView? Envelope, IReadOnlyList<string> Notes);

/// <summary>Methods are <c>actual</c>, <c>flat</c> or <c>none</c>; allowances in percent (<c>"8.25"</c>).</summary>
public sealed record PropertySaleView(
    string CostPrice, string FeesMethod, string WorksMethod, string IncomeAllowancePct, string SocialAllowancePct, IReadOnlyList<SaleOwnerView> Owners);

public sealed record SaleOwnerView(Guid Person, string Gain, string IncomeTax, string SocialTax, string Surtax);

/// <summary>A PEA's or a life insurance's remaining capital; the gain's three parts and the allowance used are a life insurance's.</summary>
public sealed record EnvelopeSaleView(string RemainingCapital, string? GainBefore2017, string? GainReduced, string? GainFull, string? AllowanceUsed);
