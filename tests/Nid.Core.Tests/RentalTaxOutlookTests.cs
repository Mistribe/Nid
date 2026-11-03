using Nid.Core.Application.Tax;
using Nid.Core.Contracts;
using Nid.Core.Domain.Tax;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Property return spec §5: what the Core tells a property's return.</summary>
public class RentalTaxOutlookTests
{
    private static readonly Guid Property = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    [Fact]
    public void Pinel_6_3_3_before_2023()
    {
        var years = RentalTaxOutlookService.PinelSchedule(new PinelCommitment(Property, 6, 12, null, false), 220_000m, 2021);

        Assert.Equal(Enumerable.Range(2021, 12), years.Keys.Order());
        Assert.All(Enumerable.Range(2021, 9), y => Assert.Equal(4_400m, years[y]));
        Assert.All(Enumerable.Range(2030, 3), y => Assert.Equal(2_200m, years[y]));
    }

    [Fact]
    public void Pinel_start_year_overrides_acquisition()
        => Assert.Equal(2022, RentalTaxOutlookService.PinelSchedule(new PinelCommitment(Property, 6, 6, 2022, false), 220_000m, 2021).Keys.Min());

    [Fact]
    public void Capital_gains_takes_net_price()
    {
        var alice = PersonId.New();
        var p = new PropertyForSale(new ItemId(Property), "Studio", "EUR", [new Owner(alice, PersonKind.Adult, 10_000)], "rental",
            new LocalDate(2015, 1, 1), 200_000m, 15_000m, 0m, null, null, false);
        var on = new LocalDate(2026, 1, 1);
        var r = PropertySale.Compute(new PropertySaleInput(PropertySaleKind.Rental, p.PurchasedOn, 200_000m, 15_000m, 0m, 250_000m,
            [new SaleOwner(alice.Value, 10_000)], on), SaleTaxRules.For(2026));

        Assert.Equal(r.IncomeTax + r.SocialTax + r.Surtax, RentalTaxOutlookService.SaleTax(p, 250_000m, on));
        Assert.True(r.IncomeTax > 0m);
    }
}
