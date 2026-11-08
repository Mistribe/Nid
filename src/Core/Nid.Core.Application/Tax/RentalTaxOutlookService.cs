using Nid.Core.Contracts;
using Nid.Core.Domain.Tax;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.Application.Tax;

/// <summary>Property return spec §5: the marginal rate of the household's estimate, the property's Pinel reductions and the sale tax.</summary>
public sealed class RentalTaxOutlookService(TaxEstimates estimates, ITaxProfileStore store) : IRentalTaxOutlook
{
    public async Task<Result<RentalTaxOutlook>> OutlookAsync(
        ItemId property, decimal costPrice, decimal? surfaceM2, LocalDate purchasedOn, LocalDate d, CancellationToken ct)
    {
        var stored = await store.FindAsync(d.Year, ct);
        // Without a profile the estimate has nothing of the household's: the caller falls back on its default rate.
        decimal? marginal = null;
        if (stored is not null && (await estimates.ComputeAsync(d.Year, VisibilityScope.Household, d, ct)).TryGetValue(out var result))
            marginal = result.MarginalRate;
        var commitment = stored?.Profile.Pinel.FirstOrDefault(c => c.Property == property.Value);
        var pinel = commitment is null ? null : new PinelOutlook(commitment.StartYear ?? purchasedOn.Year, commitment.InitialYears,
            commitment.TotalYears, PinelSchedule(commitment, Pinel.Base(costPrice, surfaceM2), purchasedOn.Year));
        return Result.Success(new RentalTaxOutlook(marginal, TaxRules.For(d.Year).RentalSocialRate, pinel));
    }

    public decimal CapitalGainsTax(PropertyForSale property, decimal netSalePrice, LocalDate on) => SaleTax(property, netSalePrice, on);

    /// <summary>The reduction of every year of the commitment, from its start year (the acquisition's when none is set).</summary>
    public static IReadOnlyDictionary<int, decimal> PinelSchedule(PinelCommitment c, decimal pinelBase, int acquisitionYear)
    {
        var start = c.StartYear ?? acquisitionYear;
        return Enumerable.Range(start, c.TotalYears).ToDictionary(y => y,
            y => Pinel.AnnualReduction(pinelBase, acquisitionYear, c.Plus, c.InitialYears, c.TotalYears, start, y));
    }

    public static decimal SaleTax(PropertyForSale p, decimal netSalePrice, LocalDate on)
    {
        var r = PropertySale.Compute(new PropertySaleInput(SaleTaxEstimates.KindOf(p.Type), p.PurchasedOn, p.PurchasePrice, p.ActualFees,
            p.ActualWorks, netSalePrice, p.Owners.Select(o => new SaleOwner(o.PersonId.Value, o.ShareBp)).ToList(), on), SaleTaxRules.For(on.Year));
        return r.IncomeTax + r.SocialTax + r.Surtax;
    }
}
