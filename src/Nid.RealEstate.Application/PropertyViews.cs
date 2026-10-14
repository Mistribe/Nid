using System.Globalization;
using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using static Nid.RealEstate.Application.LoanReading;

namespace Nid.RealEstate.Application;

/// <summary>A row of <c>GET /properties</c> (spec 3C1 §4): the value today and the net value, both null without an estimate.</summary>
public sealed record PropertySummary(
    ItemId Id, string Name, string Type, string Currency, string Visibility, string? Value, LocalDate? ValueOn, bool Stale,
    string? NetValue, string? GrossYield, string? VacancyRate, bool CanWrite);

public sealed record PropertyList(IReadOnlyList<PropertySummary> Items) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.Select(i => i.Id);
}

public sealed record PropertyOwnerView(Guid PersonId, int ShareBp);

/// <summary>An estimate, under an envelope's member names (precision 1).</summary>
public sealed record PropertyValuationView(Guid Id, LocalDate AsOf, string Amount, string? Note);

public sealed record PropertyFlowView(Guid Id, LocalDate OccurredOn, string Kind, string Amount, string? Category, string? Note);

/// <summary>A planned cost's period, <c>{ count, unit }</c> as the body sends it (<c>month</c> or <c>year</c>).</summary>
public sealed record PropertyEveryView(int Count, string Unit);

/// <summary>A planned cost (spec 3C2 §3): its monthly equivalent, and its next due date on or after today (null once ended).</summary>
public sealed record PropertyRecurringCostView(
    Guid Id, uint Version, string Category, string Amount, PropertyEveryView Every, LocalDate StartsOn, LocalDate? EndsOn, string? Note,
    string MonthlyEquivalent, LocalDate? NextDueOn);
/// <summary>A tenancy (spec 3C2 §3); <c>Current</c>: it covers today.</summary>
public sealed record PropertyTenancyView(
    Guid Id, uint Version, string Label, LocalDate StartsOn, LocalDate? EndsOn, string RentMonthly, string ChargesMonthly, string? Deposit,
    string? Note, bool Current);

/// <summary>A linked loan the reader can read: its balance today, and in the property's currency (null when the rate is missing).</summary>
public sealed record PropertyLoanView(ItemId Id, string Name, string Currency, string Balance, string? BalanceConverted);

public sealed record PropertyCostLineView(string Code, string Amount);

public sealed record PropertyWarningView(string Code, ItemId? LoanId);

public sealed record PropertyFiguresView(
    string? Value, LocalDate? ValueOn, bool Stale, string CostPrice, string? LatentGain, string LinkedBalance, string? NetValue,
    string RealCost, IReadOnlyList<PropertyCostLineView> RealCostLines, string NetCostOfRents,
    LocalDate? RentedSince, string ExpectedRent, string RentReceived, string Arrears, int? VacantDays, string? VacancyRate,
    string? GrossYield, string? NetYieldBeforeTax);

/// <summary>Spec 3C1 §4: shaped like an envelope's detail.</summary>
public sealed record PropertyDetail(
    ItemId Id, string Name, string Type, string Currency, string Visibility, IReadOnlyList<PropertyOwnerView> Owners,
    LocalDate PurchaseDate, string PurchasePrice, string NotaryFees, string AgencyFees, string OtherAcquisitionCosts, string? SurfaceM2,
    string? LocationLabel, string ValuationCadence, int? StaleAfterDays, LocalDate? RentalStartsOn, string? Note, uint Version,
    PropertyFiguresView Figures, IReadOnlyList<PropertyWarningView> Warnings, IReadOnlyList<PropertyValuationView> Valuations,
    IReadOnlyList<PropertyFlowView> Flows, IReadOnlyList<PropertyRecurringCostView> RecurringCosts, IReadOnlyList<PropertyTenancyView> Tenancies,
    IReadOnlyList<PropertyLoanView> Loans, bool CanWrite) : IExposesItems
{
    /// <summary>The property and its readable linked loans; every warning's loan is one of them.</summary>
    public IEnumerable<ItemId> ExposedItems() => [Id, .. Loans.Select(l => l.Id)];
}

/// <summary>Pure builders of the views; every amount written once, two decimals, invariant (precision 7).</summary>
internal static class PropertyViews
{
    public static PropertySummary Summary(ItemAccess item, StoredProperty property, PropertyFigures f, bool canWrite)
        => new(property.ItemId, item.DisplayName, LoanWire.Of(property.Details.Type), LoanViews.CurrencyOf(item).Code,
            LoanViews.Wire(item.Visibility), Amount(f.Value), f.ValueOn, f.Stale, Amount(f.NetValue), Fraction(f.Rental.GrossYield),
            Fraction(f.Rental.VacancyRate), canWrite);

    public static PropertyDetail Detail(ItemAccess item, StoredProperty property, PropertyRead read, bool canWrite, LocalDate today)
    {
        var d = property.Details;
        var f = read.Figures;
        return new PropertyDetail(property.ItemId, item.DisplayName, LoanWire.Of(d.Type), LoanViews.CurrencyOf(item).Code,
            LoanViews.Wire(item.Visibility), item.Owners.Select(o => new PropertyOwnerView(o.PersonId.Value, o.ShareBp)).ToList(),
            d.PurchaseDate, Amount(d.PurchasePrice), Amount(d.NotaryFees), Amount(d.AgencyFees), Amount(d.OtherAcquisitionCosts),
            Amount(d.SurfaceM2), d.LocationLabel, LoanWire.Of(d.Cadence), d.StaleAfterDays, d.RentalStartsOn, d.Note, property.Version,
            new PropertyFiguresView(Amount(f.Value), f.ValueOn, f.Stale, Amount(f.CostPrice), Amount(f.LatentGain), Amount(f.LinkedBalance),
                Amount(f.NetValue), Amount(f.RealCost), f.RealCostLines.Select(l => new PropertyCostLineView(l.Code, Amount(l.Amount))).ToList(),
                Amount(f.NetCostOfRents), f.Rental.RentedSince, Amount(f.Rental.ExpectedRent), Amount(f.Rental.RentReceived),
                Amount(f.Rental.Arrears), f.Rental.VacantDays, Fraction(f.Rental.VacancyRate), Fraction(f.Rental.GrossYield),
                Fraction(f.Rental.NetYieldBeforeTax)),
            f.Warnings.Select(w => new PropertyWarningView(w.Code, w.LoanId)).ToList(),
            read.Values.OrderBy(v => v.AsOf).Select(v => new PropertyValuationView(v.Id, v.AsOf, Amount(v.Value.Amount), v.Note)).ToList(),
            read.Flows.Select(x => new PropertyFlowView(x.Id, x.OccurredOn, LoanWire.Of(x.Kind), Amount(x.Amount.Amount), x.Category, x.Note)).ToList(),
            property.Costs.Select(c => new PropertyRecurringCostView(c.Id, c.Version, c.Cost.Category, Amount(c.Cost.Amount),
                new PropertyEveryView(c.Cost.Every.Count, LoanWire.Of(c.Cost.Every.Unit)), c.Cost.StartsOn, c.Cost.EndsOn, c.Cost.Note,
                Amount(PropertyMath.MonthlyEquivalent(c.Cost)), PropertyMath.NextDueOn(c.Cost, today))).ToList(),
            property.Tenancies.Select(t => new PropertyTenancyView(t.Id, t.Version, t.Tenancy.Label, t.Tenancy.StartsOn, t.Tenancy.EndsOn,
                Amount(t.Tenancy.RentMonthly), Amount(t.Tenancy.ChargesMonthly), Amount(t.Tenancy.Deposit), t.Tenancy.Note,
                PropertyMath.Covers(t.Tenancy, today))).ToList(),
            read.Loans.Zip(f.Loans, (l, b) => new PropertyLoanView(l.Id, l.DisplayName, LoanViews.CurrencyOf(l).Code, Amount(b.Balance), Amount(b.Converted))).ToList(),
            canWrite);
    }

    /// <summary>Spec 3C2 §4: a rate on the wire, a fraction with four decimals, invariant (<c>"0.0384"</c>), rounded once here.</summary>
    private static string? Fraction(decimal? x) => x?.ToString("0.0000", CultureInfo.InvariantCulture);
}
