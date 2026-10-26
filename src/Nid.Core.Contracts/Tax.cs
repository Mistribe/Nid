using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

/// <summary>
/// Tax estimate spec §6: one figure a module holds for a revenue year, in euros. <c>Person</c> is set when the figure is a
/// person's (a salary, a PER contribution: one line per owner, at their share); <c>On</c> and <c>Surface</c> describe a
/// rental property (its purchase date, its surface). Rental 2044 spec §4: <c>On</c>, <c>Category</c> and <c>Note</c> describe a flow
/// « À vérifier » (<see cref="TaxLineCodes.RentalToCheck"/>).
/// </summary>
public sealed record TaxLine(
    string Code, ItemId Item, string Name, PersonId? Person, decimal Amount, LocalDate? On = null, decimal? Surface = null,
    string? Category = null, string? Note = null);

/// <summary>Tax estimate spec §6: the codes of <see cref="TaxLine"/>.</summary>
public static class TaxLineCodes
{
    public const string SalaryGross = "salary_gross";
    /// <summary>A phase without its gross: the net paid stands in, with a warning.</summary>
    public const string SalaryNet = "salary_net";
    public const string SalaryOneOff = "salary_one_off";
    /// <summary>Tax-per spec §3.3: a payment's net taxable income (« net imposable »), entered on the phase or the one-off.</summary>
    public const string SalaryTaxable = "salary_taxable";
    /// <summary>One line per payment (<c>On</c>, <c>Note</c>, <c>Category</c>: <see cref="DetailEntered"/>, <see cref="DetailGross"/> or <see cref="DetailNet"/>): shown, never summed.</summary>
    public const string SalaryDetail = "salary_detail";
    public const string DetailEntered = "entered";
    public const string DetailGross = "gross";
    public const string DetailNet = "net";
    /// <summary>The gross and the net taxable of the phase payments that hold both: the observed rate between them, never summed into the estimate.</summary>
    public const string SalaryRateGross = "salary_rate_gross";
    public const string SalaryRateTaxable = "salary_rate_taxable";
    public const string OtherTaxable = "other_taxable";
    public const string NonTaxable = "non_taxable";
    /// <summary>An income the estimate does not compute (pension, self-employment, alimony): shown, never counted.</summary>
    public const string Unsupported = "unsupported";
    /// <summary>Rental 2044 spec §3: a property's amount on a line of the form; <c>Item</c> is the property, a loan's interest included.</summary>
    public const string Rental211 = "rental_211";
    public const string Rental221 = "rental_221";
    public const string Rental222 = "rental_222";
    public const string Rental223 = "rental_223";
    public const string Rental224 = "rental_224";
    public const string Rental227 = "rental_227";
    public const string Rental229 = "rental_229";
    public const string Rental250 = "rental_250";
    /// <summary>Rental 2044 spec §3: one expense « À vérifier » (<c>works</c>, <c>other</c>), out of the totals.</summary>
    public const string RentalToCheck = "rental_to_check";
    /// <summary>Rental 2044 spec §4: a property's charges taken out of rents recorded with them.</summary>
    public const string RentalChargesRemoved = "rental_charges_removed";
    /// <summary>Rental 2044 spec §3: a loan's early-repayment penalties and other fees of the year, not counted on line 250.</summary>
    public const string RentalFeeNotCounted = "rental_fee_not_counted";
    /// <summary>A rental property: its cost price, purchase date and surface (the 20 € per property, Pinel's base).</summary>
    public const string RentalProperty = "rental_property";
    public const string CapitalIncome = "capital_income";
    public const string PerContribution = "per_contribution";
    /// <summary>Tax-per spec §4.5: a PER open in the year with a value and neither a payment nor an opening in it (amount 0, one line per owner).</summary>
    public const string PerNoContribution = "per_no_contribution";
    /// <summary>A withdrawal from a PEA, a life insurance or a PER: its tax is not estimated.</summary>
    public const string Withdrawal = "withdrawal";
    /// <summary>An item outside the euro: left out.</summary>
    public const string OtherCurrency = "other_currency";

    /// <summary>One line per owner at their share, or one line without a person when the item has no owner.</summary>
    public static IEnumerable<TaxLine> Split(string code, ItemAccess item, decimal amount)
        => item.Owners.Count == 0
            ? [new TaxLine(code, item.Id, item.DisplayName, null, amount)]
            : item.Owners.Select(o => new TaxLine(code, item.Id, item.DisplayName, o.PersonId, amount * o.ShareBp / 10_000m));

    /// <summary>1 January to 31 December of the revenue year.</summary>
    public static DateRange Year(int year)
        => DateRange.Create(new LocalDate(year, 1, 1), new LocalDate(year + 1, 1, 1)).TryGetValue(out var r) ? r : throw new InvalidOperationException();

    /// <summary>Rental 2044 spec §2: the <c>Category</c> of an <see cref="OtherCurrency"/> line left out of the 2044 (a rental property, its loan).</summary>
    public const string Rental2044 = "rental_2044";

    /// <summary>
    /// The year's lines are in euros only: another currency gives one <see cref="OtherCurrency"/> line, of <paramref name="category"/>
    /// (<see cref="Rental2044"/> for RealEstate's items).
    /// </summary>
    public static bool InEuros(ItemAccess item, ICollection<TaxLine> lines, string? category = null)
    {
        if (item.Currency?.Trim() is null or "EUR")
            return true;
        lines.Add(new TaxLine(OtherCurrency, item.Id, item.DisplayName, null, 0m, Category: category));
        return false;
    }
}

/// <summary>Tax estimate spec §6: a module's figures for revenue year <c>year</c>, over the items the reader sees in the scope.</summary>
public interface ITaxContribution
{
    Task<Result<IReadOnlyList<TaxLine>>> TaxLinesAsync(PersonId reader, VisibilityScope scope, int year, LocalDate today, CancellationToken ct);
}

/// <summary>Sale tax spec §6: what a module supplies about the items the reader sees in the scope, to estimate the tax if each were sold today.</summary>
public interface ISaleContribution
{
    Task<Result<SaleAssets>> SaleAssetsAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct);
}

/// <summary>A module's visible active properties and envelopes, open or closed; one module fills one list.</summary>
public sealed record SaleAssets(IReadOnlyList<PropertyForSale> Properties, IReadOnlyList<EnvelopeForSale> Envelopes);

/// <summary>
/// A property: <c>Type</c> on the wire (<c>primary_residence</c>…); <c>ActualFees</c> the notary, agency and other acquisition costs;
/// <c>ActualWorks</c> the <c>works</c> expenses dated on or before today; <c>Value</c> its latest valuation, null without one or when
/// its facts cannot be read.
/// </summary>
public sealed record PropertyForSale(
    ItemId Item, string Name, string? Currency, IReadOnlyList<Owner> Owners, string Type, LocalDate PurchasedOn, decimal PurchasePrice,
    decimal ActualFees, decimal ActualWorks, decimal? Value, LocalDate? ValueOn, bool Stale);

/// <summary>
/// An envelope's payment or withdrawal: <c>Kind</c> is <c>opening</c>, <c>contribution</c> or <c>withdrawal</c>. A transfer leg carries
/// its <c>TransferRef</c> (the legs' shared reference) and <c>CounterpartKind</c>, the other leg's envelope kind when the reader sees it.
/// </summary>
public sealed record SaleFlow(LocalDate On, string Kind, decimal Amount, string? TransferRef, string? CounterpartKind);

public sealed record SaleValue(LocalDate On, decimal Value);

/// <summary>A holding of the latest snapshot: its value (estimated when the envelope's value is), units and cost per unit (PRU).</summary>
public sealed record SaleLine(decimal Value, decimal? Units, decimal? CostPerUnit);

/// <summary>
/// An envelope: <c>Kind</c> on the wire; <c>Value</c> its value today (the estimate when newer than the latest valuation), null
/// without one or when its facts cannot be read; <c>Lines</c> the latest snapshot on or before today, dated <c>LinesOn</c>, null
/// without one. A closed one (<c>ClosedOn</c>) comes for its flows and values only, without value or lines: never sold, replayed as a
/// transfer's source.
/// </summary>
public sealed record EnvelopeForSale(
    ItemId Item, string Name, string? Currency, IReadOnlyList<Owner> Owners, string Kind, LocalDate? OpenedOn, decimal? PremiumsBefore2017,
    decimal? Value, LocalDate? ValueOn, bool Stale, bool Estimated, IReadOnlyList<SaleFlow> Flows, IReadOnlyList<SaleValue> Values,
    IReadOnlyList<SaleLine>? Lines, LocalDate? LinesOn, bool LinesComplete, LocalDate? ClosedOn);

/// <summary>Tax estimate spec §5: a rental property under Pinel.</summary>
public sealed record PinelCommitment(Guid Property, int InitialYears, int TotalYears, int? StartYear, bool Plus);

/// <summary>
/// Tax estimate spec §7: the household's declarations for a revenue year. <c>Statuses</c> maps an adult to
/// <c>non_executive</c>, <c>executive</c> or <c>public</c>; <c>Dependents</c> null counts the household's dependents;
/// <c>RentalRegime</c> is <c>micro</c>, <c>real</c> or null (micro when allowed). <c>ContributionRates</c> (fractions) and
/// <c>ActualExpenses</c> map an adult to the payslip's rate and to frais réels; absent, the status's rate and the 10 % allowance.
/// <c>PerUnusedCeilings</c> maps an adult to the unused PER ceilings of the three previous years (the avis d'imposition's);
/// <c>RentalDeficitCarried</c> is the household's earlier rental deficits not yet imputed. Tax-per spec §4.1: <c>PerCeilings</c> maps an
/// adult to the PER ceiling of the avis d'imposition (already total, the unused ceilings included), <c>PerCeilingsExpiring</c> to the
/// part of it lost on 31 December; <c>PerPooling</c>: the spouses pool their ceilings.
/// </summary>
public sealed record TaxProfile(
    IReadOnlyDictionary<Guid, string> Statuses, int? Dependents, int ChildrenUnder6, string? RentalRegime,
    IReadOnlyList<PinelCommitment> Pinel, decimal ChildcareOutsideHome, decimal HomeEmployment, decimal Donations, decimal DonationsAid,
    IReadOnlyDictionary<Guid, decimal>? ContributionRates = null, IReadOnlyDictionary<Guid, decimal>? ActualExpenses = null,
    IReadOnlyDictionary<Guid, decimal>? PerUnusedCeilings = null, decimal RentalDeficitCarried = 0m,
    IReadOnlyDictionary<Guid, decimal>? PerCeilings = null, IReadOnlyDictionary<Guid, decimal>? PerCeilingsExpiring = null, bool PerPooling = false)
{
    public static readonly TaxProfile Empty = new(new Dictionary<Guid, string>(), null, 0, null, [], 0m, 0m, 0m, 0m);
}

/// <summary>A stored profile and the revenue year it was saved for (an earlier one when the year has none yet).</summary>
public sealed record StoredTaxProfile(int Year, TaxProfile Profile);

public interface ITaxProfileStore
{
    /// <summary>The profile of <paramref name="year"/>, or of the latest year before it; null when none is stored.</summary>
    Task<StoredTaxProfile?> FindAsync(int year, CancellationToken ct);

    Task SaveAsync(int year, TaxProfile profile, CancellationToken ct);
}
