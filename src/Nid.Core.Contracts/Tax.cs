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

/// <summary>Tax estimate spec §5: a rental property under Pinel.</summary>
public sealed record PinelCommitment(Guid Property, int InitialYears, int TotalYears, int? StartYear, bool Plus);

/// <summary>
/// Tax estimate spec §7: the household's declarations for a revenue year. <c>Statuses</c> maps an adult to
/// <c>non_executive</c>, <c>executive</c> or <c>public</c>; <c>Dependents</c> null counts the household's dependents;
/// <c>RentalRegime</c> is <c>micro</c>, <c>real</c> or null (micro when allowed). <c>ContributionRates</c> (fractions) and
/// <c>ActualExpenses</c> map an adult to the payslip's rate and to frais réels; absent, the status's rate and the 10 % allowance.
/// </summary>
public sealed record TaxProfile(
    IReadOnlyDictionary<Guid, string> Statuses, int? Dependents, int ChildrenUnder6, string? RentalRegime,
    IReadOnlyList<PinelCommitment> Pinel, decimal ChildcareOutsideHome, decimal HomeEmployment, decimal Donations, decimal DonationsAid,
    IReadOnlyDictionary<Guid, decimal>? ContributionRates = null, IReadOnlyDictionary<Guid, decimal>? ActualExpenses = null)
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
