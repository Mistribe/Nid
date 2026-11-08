using NodaTime;
using static Nid.Core.Domain.Tax.SaleRounding;

namespace Nid.Core.Domain.Tax;

public enum PropertySaleKind { PrimaryResidence, Rental, Land, Other }

public sealed record SaleOwner(Guid Person, int ShareBp);

public sealed record PropertySaleInput(
    PropertySaleKind Kind, LocalDate PurchasedOn, decimal PurchasePrice, decimal ActualFees, decimal ActualWorks, decimal SalePrice,
    IReadOnlyList<SaleOwner> Owners, LocalDate Today);

/// <summary>One owner's share: allowances as fractions 0..1, every amount rounded to the euro.</summary>
public sealed record OwnerSaleTax(
    Guid Person, decimal Gain, decimal IncomeAllowance, decimal SocialAllowance, decimal IncomeBase, decimal SocialBase,
    decimal IncomeTax, decimal SocialTax, decimal Surtax);

/// <summary>Methods are <c>actual</c>, <c>flat</c> or <c>none</c>; the property's taxes are the sums of its owners' rounded figures.</summary>
public sealed record PropertySaleResult(
    bool Exempt, int YearsHeld, decimal CostPrice, string FeesMethod, string WorksMethod, decimal Gain,
    IReadOnlyList<OwnerSaleTax> Owners, decimal IncomeTax, decimal SocialTax, decimal Surtax, decimal Net, IReadOnlyList<string> Notes);

/// <summary>Sale tax spec §3: the capital gain on a property sold today (art. 150 U et s. CGI).</summary>
public static class PropertySale
{
    public static PropertySaleResult Compute(PropertySaleInput input, SaleTaxRules rules)
    {
        var years = HeldYears.FullYears(input.PurchasedOn, input.Today);
        var salePrice = Euros(input.SalePrice);
        if (input.Kind == PropertySaleKind.PrimaryResidence)
            return new PropertySaleResult(true, years, 0m, "none", "none", 0m, [], 0m, 0m, 0m, salePrice, []);

        var notes = new List<string> { "sale_costs_not_deducted" };

        // D4: the more favourable of real and flat; a tie keeps the real amount.
        var flatFees = rules.AcquisitionFlatRate * input.PurchasePrice;
        var (fees, feesMethod) = input.ActualFees >= flatFees ? (input.ActualFees, "actual") : (flatFees, "flat");

        // art. 150 VB II 4° CGI: the works flat needs more than 5 years of detention, so the 5th anniversary itself does not qualify.
        var worksFlatAllowed = input.Kind != PropertySaleKind.Land && input.Today > input.PurchasedOn.PlusYears(5);
        var flatWorks = rules.WorksFlatRate * input.PurchasePrice;
        decimal works;
        string worksMethod;
        if (input.Kind == PropertySaleKind.Rental)
        {
            // D8c: real works may have been deducted from the rents
            notes.Add("rental_works_flat_only");
            (works, worksMethod) = worksFlatAllowed ? (flatWorks, "flat") : (0m, "none");
        }
        else if (worksFlatAllowed && flatWorks > input.ActualWorks)
            (works, worksMethod) = (flatWorks, "flat");
        else
            (works, worksMethod) = (input.ActualWorks, input.ActualWorks > 0m ? "actual" : "none");

        if (input.Kind == PropertySaleKind.Land) notes.Add("land_surtax_not_applied"); // D15

        var cost = input.PurchasePrice + fees + works;
        var gain = input.SalePrice - cost;
        var incomeAllowance = PropertyAllowance.Income(years);
        var socialAllowance = PropertyAllowance.Social(years);

        // D5: exact shares, the last owner takes the remainder; each figure rounded per owner, then summed,
        // the gains apportioned so they sum to the rounded gain.
        var shares = new decimal[input.Owners.Count];
        for (var i = 0; i < shares.Length; i++)
            shares[i] = i == shares.Length - 1 ? gain - shares.Sum() : gain * input.Owners[i].ShareBp / 10_000m;
        var gains = Apportion(shares);
        var owners = new List<OwnerSaleTax>();
        for (var i = 0; i < shares.Length; i++)
        {
            var share = shares[i];
            var (incomeBase, socialBase) = share > 0m
                ? (share * (1m - incomeAllowance), share * (1m - socialAllowance))
                : (0m, 0m);
            var surtax = input.Kind == PropertySaleKind.Land ? 0m : Surtax.Of(incomeBase);
            owners.Add(new OwnerSaleTax(
                input.Owners[i].Person, gains[i], incomeAllowance, socialAllowance, Euros(incomeBase), Euros(socialBase),
                Euros(rules.PropertyIncomeRate * incomeBase), Euros(rules.PropertySocialRate * socialBase), Euros(surtax)));
        }

        var (incomeTax, socialTax, totalSurtax) = (owners.Sum(o => o.IncomeTax), owners.Sum(o => o.SocialTax), owners.Sum(o => o.Surtax));
        return new PropertySaleResult(
            false, years, Euros(cost), feesMethod, worksMethod, owners.Sum(o => o.Gain), owners,
            incomeTax, socialTax, totalSurtax, salePrice - incomeTax - socialTax - totalSurtax, notes);
    }
}
