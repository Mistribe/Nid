using System.Globalization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.RealEstate.Application.LoanReading;

namespace Nid.RealEstate.Application;

public sealed record GetPropertyReturnQuery(ItemId ItemId) : IQuery<PropertyReturnView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

public sealed record SavePropertyReturnAssumptionsCommand(ItemId ItemId, ReturnAssumptions Assumptions) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record ClearPropertyReturnAssumptionsCommand(ItemId ItemId) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>Spec §3 on the wire: rates as fractions (0.0000), amounts as strings; <c>Defaulted</c> names the fields that took their default.</summary>
public sealed record ReturnAssumptionsView(
    int YearsAfterPinel, string? ResalePrice, string ValueGrowth, string RentGrowth, string ChargesGrowth, int VacancyMonths,
    string? RentAfterPinel, string MarginalRate, string ResaleFees, IReadOnlyList<string> Defaulted);

/// <summary>Spec §4.5: <c>Irr</c> in percent at 0.1, null when not meaningful.</summary>
public sealed record ReturnOutcomeView(LocalDate ResaleOn, string ResalePrice, string? Irr, string NetGain, bool PinelClawback);

public sealed record ReturnBreakdownLineView(string Line, string Realized, string Projected);

public sealed record ReturnScenarioView(ReturnOutcomeView Outcome, string PriceSource, string SavingsEffort, IReadOnlyList<ReturnBreakdownLineView> Breakdown);

/// <summary>A <c>loan_excluded</c> warning names a loan: the output guard checks it like any other item the response carries.</summary>
public sealed record PropertyReturnView(
    ReturnAssumptionsView Assumptions, ReturnScenarioView? Scenario, IReadOnlyList<ReturnOutcomeView> Sensitivity, LocalDate? PinelEnd,
    IReadOnlyList<PropertyWarning> Warnings) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Warnings.Where(w => w.LoanId is not null).Select(w => w.LoanId!.Value);
}

/// <summary>Property return spec §4.3 and §5: the property as the tax rules read it.</summary>
public static class ReturnSale
{
    /// <summary>Pinel's base as the tax estimate takes it: the purchase and its fees, without works.</summary>
    public static decimal PinelCostPrice(PropertyDetails d) => d.PurchasePrice + d.NotaryFees + d.AgencyFees + d.OtherAcquisitionCosts;

    /// <summary>
    /// No actual works: the return deducts them from the rents already, and art. 150 VB II 4° CGI keeps works taken into account
    /// for income tax out of the acquisition price; the 15 % flat rate still applies past 5 years.
    /// </summary>
    public static PropertyForSale ForSale(ItemAccess item, PropertyDetails d) => new(item.Id, item.DisplayName, item.Currency, item.Owners,
        LoanWire.Of(d.Type), d.PurchaseDate, d.PurchasePrice, d.NotaryFees + d.AgencyFees + d.OtherAcquisitionCosts, 0m, null, null, false);
}

/// <summary>Property return spec §3: the defaults, the marginal rate from the household's estimate when there is one.</summary>
public static class ReturnDefaults
{
    public const decimal MarginalRate = 0.30m;

    public static EffectiveAssumptions Apply(ReturnAssumptions? stored, decimal? estimateRate)
    {
        var a = stored ?? new ReturnAssumptions();
        var defaulted = new HashSet<string>();
        T Or<T>(T? value, T fallback, string field) where T : struct
        {
            if (value is { } v) return v;
            defaulted.Add(field);
            return fallback;
        }
        if (a.ResalePrice is null) defaulted.Add("resalePrice");
        if (a.RentAfterPinel is null) defaulted.Add("rentAfterPinel");
        return new EffectiveAssumptions(
            Or(a.YearsAfterPinel, 0, "yearsAfterPinel"), a.ResalePrice, Or(a.ValueGrowth, 0.01m, "valueGrowth"), Or(a.RentGrowth, 0.01m, "rentGrowth"),
            Or(a.ChargesGrowth, 0.02m, "chargesGrowth"), Or(a.VacancyMonths, 0, "vacancyMonths"), a.RentAfterPinel,
            Or(a.MarginalRate, estimateRate ?? MarginalRate, "marginalRate"), Or(a.ResaleFees, 0.05m, "resaleFees"), defaulted);
    }
}

[RequestHandler<GetPropertyReturnQuery, PropertyReturnView>]
public sealed class GetPropertyReturnHandler(
    IPropertyStore properties, IItemRegistry items, PropertyReading reading, IHouseholdCalendar calendar, IActorAccessor actor, IRentalTaxOutlook tax)
    : IRequestHandler<GetPropertyReturnQuery, PropertyReturnView>
{
    private static readonly string[] Fields =
        ["yearsAfterPinel", "resalePrice", "valueGrowth", "rentGrowth", "chargesGrowth", "vacancyMonths", "rentAfterPinel", "marginalRate", "resaleFees"];

    public async ValueTask<Result<PropertyReturnView>> HandleAsync(GetPropertyReturnQuery query, CancellationToken ct = default)
    {
        var loaded = await PropertyCheck.LoadAsync(properties, items, query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<PropertyReturnView>(failure!);
        loaded.TryGetValue(out var found);
        var today = await calendar.TodayAsync(ct);
        var read = await reading.ReadAsync(found.Property, found.Item, actor.Current!.PersonId!.Value, today, ct);
        if (read.TryGetFailure(out failure))
            return Result.Failure<PropertyReturnView>(failure!);
        read.TryGetValue(out var r);
        var d = found.Property.Details;
        var outlook = await tax.OutlookAsync(query.ItemId, ReturnSale.PinelCostPrice(d), d.SurfaceM2, d.PurchaseDate, today, ct);
        if (outlook.TryGetFailure(out failure))
            return Result.Failure<PropertyReturnView>(failure!);
        outlook.TryGetValue(out var o);

        var assumptions = ReturnDefaults.Apply(await properties.FindReturnAssumptionsAsync(query.ItemId, ct), o!.MarginalRate);
        var forSale = ReturnSale.ForSale(found.Item, d);
        var result = PropertyReturn.Compute(new ReturnInputs(d, r!.Flows, found.Property.Tenancies.Select(t => t.Tenancy).ToList(),
            r.Values.Where(v => v.AsOf <= today).MaxBy(v => v.AsOf),
            r.Sources.Where(s => s.Rate is not null).Select(s => new ReturnLoan(s.Item.Id, s.Terms, s.Schedule, s.Rate!.Value)).ToList(),
            assumptions, o.RentalSocialRate, o.Pinel, (net, on) => tax.CapitalGainsTax(forSale, net, on), today));

        var warnings = result.Warnings.Select(w => new PropertyWarning(w)).ToList();
        if (o.MarginalRate is null && assumptions.Defaulted.Contains("marginalRate"))
            warnings.Add(new PropertyWarning("marginal_rate_default"));
        warnings.AddRange(r.Sources.Where(s => s.Rate is null).Select(s => new PropertyWarning("loan_excluded", s.Item.Id)));
        return Result.Success(new PropertyReturnView(View(assumptions), result.Scenario is { } s ? View(s) : null,
            result.Sensitivity.Select(View).ToList(), result.PinelEnd, warnings));
    }

    private static ReturnAssumptionsView View(EffectiveAssumptions a) => new(
        a.YearsAfterPinel, Amount(a.ResalePrice), Fraction(a.ValueGrowth), Fraction(a.RentGrowth), Fraction(a.ChargesGrowth), a.VacancyMonths,
        Amount(a.RentAfterPinel), Fraction(a.MarginalRate), Fraction(a.ResaleFees), Fields.Where(a.Defaulted.Contains).ToList());

    private static ReturnScenarioView View(ReturnScenario s) => new(View(s.Outcome), s.PriceSource, Amount(s.SavingsEffort),
        s.Breakdown.Select(b => new ReturnBreakdownLineView(b.Line, Amount(b.Realized), Amount(b.Projected))).ToList());

    private static ReturnOutcomeView View(ReturnOutcome o)
        => new(o.ResaleOn, Amount(o.ResalePrice), o.IrrPercent?.ToString("0.0", CultureInfo.InvariantCulture), Amount(o.NetGain), o.PinelClawback);

    private static string Fraction(decimal x) => x.ToString("0.0000", CultureInfo.InvariantCulture);
}

[RequestHandler<SavePropertyReturnAssumptionsCommand, ItemId>]
public sealed class SavePropertyReturnAssumptionsHandler(IPropertyStore properties, IItemRegistry items)
    : IRequestHandler<SavePropertyReturnAssumptionsCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(SavePropertyReturnAssumptionsCommand command, CancellationToken ct = default)
    {
        if (PropertyRules.ReturnAssumptions(command.Assumptions) is { } refused)
            return Result.Failure<ItemId>(refused);
        var loaded = await PropertyCheck.LoadAsync(properties, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        await properties.SaveReturnAssumptionsAsync(command.ItemId, command.Assumptions, ct);
        return Result.Success(command.ItemId);
    }
}

[RequestHandler<ClearPropertyReturnAssumptionsCommand, ItemId>]
public sealed class ClearPropertyReturnAssumptionsHandler(IPropertyStore properties, IItemRegistry items)
    : IRequestHandler<ClearPropertyReturnAssumptionsCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(ClearPropertyReturnAssumptionsCommand command, CancellationToken ct = default)
    {
        var loaded = await PropertyCheck.LoadAsync(properties, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        await properties.SaveReturnAssumptionsAsync(command.ItemId, null, ct);
        return Result.Success(command.ItemId);
    }
}
