using Nid.Core.Contracts;
using Nid.RealEstate.Application;
using Nid.RealEstate.Contracts;
using NodaTime;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.RealEstate.Tests;

/// <summary>Spec 3C1 §2–§3: the bounds of a property's fields and of its flows, each at the body's field.</summary>
public class PropertyRulesTests
{
    private static readonly LocalDate Today = new(2025, 10, 15);

    private static PropertyDetails Home() => new(PropertyType.PrimaryResidence, new LocalDate(2020, 6, 15), 250_000m, 19_000m, 0m, 0m,
        85.5m, "Nantes", PropertyCadence.Yearly, null, null, "achat");

    [Fact]
    public void A_sound_property_passes()
        => Assert.Null(PropertyRules.Details(Home(), Today));

    [Fact]
    public void Each_refusal_names_its_field()
    {
        var cases = new (string Why, PropertyDetails Details, string Field)[]
        {
            ("a purchase tomorrow", Home() with { PurchaseDate = Today.PlusDays(1) }, "purchaseDate"),
            ("a purchase in 1899", Home() with { PurchaseDate = new LocalDate(1899, 12, 31) }, "purchaseDate"),
            ("a free purchase", Home() with { PurchasePrice = 0m }, "purchasePrice"),
            ("a third decimal", Home() with { PurchasePrice = 250_000.005m }, "purchasePrice"),
            ("a trillion", Home() with { PurchasePrice = 1_000_000_000_000m }, "purchasePrice"),
            ("negative notary fees", Home() with { NotaryFees = -1m }, "notaryFees"),
            ("negative agency fees", Home() with { AgencyFees = -0.01m }, "agencyFees"),
            ("negative other costs", Home() with { OtherAcquisitionCosts = -5m }, "otherAcquisitionCosts"),
            ("no surface", Home() with { SurfaceM2 = 0m }, "surfaceM2"),
            ("a surface to the thousandth", Home() with { SurfaceM2 = 85.505m }, "surfaceM2"),
            ("a full address", Home() with { LocationLabel = new string('x', 101) }, "locationLabel"),
            ("a threshold of no day", Home() with { StaleAfterDays = 0 }, "staleAfterDays"),
            ("a threshold of 3651 days", Home() with { StaleAfterDays = 3651 }, "staleAfterDays"),
            ("a rental before the purchase", Home() with { RentalStartsOn = new LocalDate(2020, 6, 14) }, "rentalStartsOn"),
            ("a long note", Home() with { Note = new string('x', 2001) }, "note"),
        };

        foreach (var (why, details, field) in cases)
        {
            var refused = PropertyRules.Details(details, Today);
            Assert.True(refused is { Code: PropertyCodes.InvalidPropertyField } && Loans.FieldOf(refused) == field,
                $"{why}: expected {field}, got {refused?.Code} at {(refused is null ? null : Loans.FieldOf(refused))}");
        }
    }

    [Fact]
    public void The_bounds_themselves_pass()
    {
        Assert.Null(PropertyRules.Details(Home() with
        {
            PurchaseDate = Today, PurchasePrice = 0.01m, SurfaceM2 = null, LocationLabel = new string('x', 100), StaleAfterDays = 3650,
            RentalStartsOn = Today, Note = new string('x', 2000),
        }, Today));
        Assert.Null(PropertyRules.Details(Home() with { PurchaseDate = new LocalDate(1900, 1, 1), StaleAfterDays = 1 }, Today));
    }

    [Fact]
    public void A_rental_start_before_the_purchase_answers_where_the_caller_says()
    {
        var early = Home() with { RentalStartsOn = new LocalDate(2020, 6, 14) };
        Assert.Equal("rentalStartsOn", Loans.FieldOf(PropertyRules.Details(early, Today)!));
        Assert.Equal("purchaseDate", Loans.FieldOf(PropertyRules.Details(early, Today, "purchaseDate")!));
    }

    [Fact]
    public void A_name_is_1_to_100_characters_and_not_only_whitespace()
    {
        Assert.Null(PropertyRules.Name("Maison"));
        foreach (var bad in new[] { null, "", "   ", new string('x', 101) })
            Assert.Equal("name", Loans.FieldOf(PropertyRules.Name(bad)!));
    }

    [Fact]
    public void A_planned_cost_can_be_management_fees_but_not_works()
    {
        Assert.Contains("management_fees", PropertyRules.RecurringCategories);
        Assert.DoesNotContain("works", PropertyRules.RecurringCategories);
    }

    [Theory]
    [InlineData("expense", "works", null)]
    [InlineData("expense", "property_tax", null)]
    [InlineData("income", "rent", null)]
    [InlineData("income", "other_income", null)]
    [InlineData("expense", "management_fees", null)]
    [InlineData("income", "recoverable_charges", null)]
    [InlineData("income", "management_fees", "category")]
    [InlineData("contribution", "works", "kind")]
    [InlineData(null, "works", "kind")]
    [InlineData("expense", "rent", "category")]
    [InlineData("income", "works", "category")]
    [InlineData("expense", null, "category")]
    public void A_flow_category_matches_its_kind(string? kind, string? category, string? refusedAt)
    {
        var refused = PropertyRules.Category(kind, category);
        Assert.Equal(refusedAt, refused is null ? null : Loans.FieldOf(refused));
        if (refused is not null)
            Assert.Equal(PropertyCodes.InvalidCategory, refused.Code);
    }

    [Fact]
    public void A_change_keeps_what_it_does_not_send_and_clears_what_it_is_told_to()
    {
        var changed = new PropertyChange(PurchasePrice: 255_000m, ClearSurfaceM2: true, LocationLabel: "Rezé", ClearNote: true).ApplyTo(Home());
        Assert.Equal(Home() with { PurchasePrice = 255_000m, SurfaceM2 = null, LocationLabel = "Rezé", Note = null }, changed);
    }

    private static RecurringCostDraft Tax() => new("property_tax", 1_200m, 1, DurationUnit.Year, new LocalDate(2021, 10, 15), null, "taxe");

    [Fact]
    public void A_recurring_cost_refusal_names_its_field()
    {
        Assert.Null(PropertyRules.CostFields(Tax()));
        Assert.Null(PropertyRules.CostFields(Tax() with
        {
            EveryCount = 120, EveryUnit = DurationUnit.Month, EndsOn = new LocalDate(2021, 10, 16), Note = new string('x', 2000),
        }));
        var cases = new (string Why, RecurringCostDraft Draft, string Code, string Field)[]
        {
            ("works", Tax() with { Category = PropertyCategories.Works }, PropertyCodes.InvalidCategory, "category"),
            ("rent", Tax() with { Category = PropertyCategories.Rent }, PropertyCodes.InvalidCategory, "category"),
            ("no category", Tax() with { Category = null }, PropertyCodes.InvalidCategory, "category"),
            ("nothing to pay", Tax() with { Amount = 0m }, PropertyCodes.InvalidPropertyField, "amount"),
            ("a third decimal", Tax() with { Amount = 1.005m }, PropertyCodes.InvalidPropertyField, "amount"),
            ("every 0 years", Tax() with { EveryCount = 0 }, PropertyCodes.InvalidPropertyField, "every.count"),
            ("every 121 months", Tax() with { EveryCount = 121, EveryUnit = DurationUnit.Month }, PropertyCodes.InvalidPropertyField, "every.count"),
            ("every week", Tax() with { EveryUnit = DurationUnit.Week }, PropertyCodes.InvalidPropertyField, "every.unit"),
            ("every day", Tax() with { EveryUnit = DurationUnit.Day }, PropertyCodes.InvalidPropertyField, "every.unit"),
            ("a start in 1899", Tax() with { StartsOn = new LocalDate(1899, 12, 31) }, PropertyCodes.InvalidPropertyField, "startsOn"),
            ("an end in 2201", Tax() with { EndsOn = new LocalDate(2201, 1, 1) }, PropertyCodes.InvalidPropertyField, "endsOn"),
            ("an end on its start", Tax() with { EndsOn = new LocalDate(2021, 10, 15) }, PropertyCodes.InvalidPropertyField, "endsOn"),
            ("a long note", Tax() with { Note = new string('x', 2001) }, PropertyCodes.InvalidPropertyField, "note"),
        };

        foreach (var (why, draft, code, field) in cases)
        {
            var refused = PropertyRules.CostFields(draft);
            Assert.True(refused is not null && refused.Code == code && Loans.FieldOf(refused) == field,
                $"{why}: expected {code} at {field}, got {refused?.Code} at {(refused is null ? null : Loans.FieldOf(refused))}");
        }
    }

    [Fact]
    public void A_checked_draft_becomes_a_cost_with_the_cores_period()
        => Assert.Equal(new RecurringCost("property_tax", 1_200m, Loans.Ok(Duration.Create(1, DurationUnit.Year)), new LocalDate(2021, 10, 15), null, "taxe"),
            Tax().ToCost());

    private static Tenancy Lease(LocalDate starts, LocalDate? ends = null) => new("T2", starts, ends, 900m, 0m, null, null);

    [Fact]
    public void A_tenancy_refusal_names_its_field()
    {
        var purchase = new LocalDate(2020, 6, 15);
        var sound = new Tenancy("T2 meublé", new LocalDate(2021, 1, 1), null, 900m, 50m, 1_800m, "bail");
        Assert.Null(PropertyRules.TenancyFields(sound, purchase));
        Assert.Null(PropertyRules.TenancyFields(sound with { StartsOn = purchase, Label = new string('x', 100), ChargesMonthly = 0m, Deposit = null }, purchase));
        var cases = new (string Why, Tenancy Tenancy, string Field)[]
        {
            ("a blank label", sound with { Label = "  " }, "label"),
            ("a label of 101 characters", sound with { Label = new string('x', 101) }, "label"),
            ("a start before the purchase", sound with { StartsOn = new LocalDate(2020, 6, 14) }, "startsOn"),
            ("an end on its start", sound with { EndsOn = new LocalDate(2021, 1, 1) }, "endsOn"),
            ("an end in 2201", sound with { EndsOn = new LocalDate(2201, 1, 1) }, "endsOn"),
            ("a free tenancy", sound with { RentMonthly = 0m }, "rentMonthly"),
            ("a rent to the thousandth", sound with { RentMonthly = 900.005m }, "rentMonthly"),
            ("negative charges", sound with { ChargesMonthly = -0.01m }, "chargesMonthly"),
            ("no deposit", sound with { Deposit = 0m }, "deposit"),
            ("a long note", sound with { Note = new string('x', 2001) }, "note"),
        };

        foreach (var (why, tenancy, field) in cases)
        {
            var refused = PropertyRules.TenancyFields(tenancy, purchase);
            Assert.True(refused is { Code: PropertyCodes.InvalidPropertyField } && Loans.FieldOf(refused) == field,
                $"{why}: expected {field}, got {refused?.Code} at {(refused is null ? null : Loans.FieldOf(refused))}");
        }
    }

    [Fact]
    public void Back_to_back_tenancies_do_not_overlap_and_an_open_one_does() // Review Focus #1
    {
        var first = Lease(new LocalDate(2021, 1, 1), new LocalDate(2022, 12, 31));
        Assert.Null(PropertyRules.Overlap(Lease(new LocalDate(2023, 1, 1)), [first]));
        Assert.Null(PropertyRules.Overlap(Lease(new LocalDate(2020, 7, 1), new LocalDate(2020, 12, 31)), [first]));
        var refused = PropertyRules.Overlap(Lease(new LocalDate(2022, 12, 31)), [first]);
        Assert.Equal((PropertyCodes.TenancyOverlap, "startsOn"), (refused!.Code, Loans.FieldOf(refused)));
        Assert.NotNull(PropertyRules.Overlap(Lease(new LocalDate(2030, 1, 1)), [Lease(new LocalDate(2023, 1, 1))])); // an open one runs on
        Assert.Null(PropertyRules.Overlap(Lease(new LocalDate(2021, 1, 1)), []));
    }
}
