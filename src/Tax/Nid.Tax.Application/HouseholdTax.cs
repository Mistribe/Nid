using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using Nid.Tax.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Tax.Application;

/// <summary>#512: what Core's screens read of the tax module, from the same services the tax page uses.</summary>
public sealed class HouseholdTax(
    TaxEstimates estimates, SaleTaxEstimates sales, PerDeadline perDeadline, TaxCalendarEvents calendar, ITaxAssessmentStore assessments) : IHouseholdTax
{
    public async Task<Result<TaxYearFigures>> YearAsync(int year, VisibilityScope scope, LocalDate today, CancellationToken ct)
    {
        if (year < TaxRules.FirstYear) return Result.Success(TaxYearFigures.None);
        return (await estimates.ComputeWithDataAsync(year, scope, today, ct)).Map(c =>
            new TaxYearFigures(c.HasData, c.Result.TotalTax, c.Result.IncomeTax, c.Result.SocialLevies, c.Result.CapitalFlatTax));
    }

    public async Task<Result<decimal>> RentalPurchaseAsync(VisibilityScope scope, LocalDate today, RentalPurchaseTax purchase, CancellationToken ct)
    {
        var read = await estimates.InputsAsync(today.Year, scope, today, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<decimal>(failure!);
        read.TryGetValue(out var inputs);
        var regime = TaxWire.TryRegime(purchase.RentalRegime, out var chosen) ? chosen : inputs!.RentalRegime;
        var real = regime == RentalRegime.Real;
        var replayed = inputs! with
        {
            RentalGross = inputs.RentalGross + purchase.Rents,
            RentalCharges = inputs.RentalCharges + (real ? purchase.Charges + 20m : 0m),   // + the engine's flat charge per rental (TaxEstimates)
            RentalInterest = inputs.RentalInterest + (real ? purchase.LoanInterest : 0m),
            RentalRegime = regime,
        };
        return Result.Success((TaxCalculator.Compute(replayed).TotalTax - TaxCalculator.Compute(inputs).TotalTax) / 12m);
    }

    public Task<Result<SaleItemView>> PropertySaleAsync(PersonId reader, ItemId property, LocalDate today, LocalDate on, decimal netPrice, CancellationToken ct)
        => sales.PropertyAsync(reader, property, today, on, netPrice, ct);

    public Task<Result<IReadOnlyList<DashboardEventView>>> PerDeadlineAsync(PersonId reader, VisibilityScope scope, LocalDate today, LocalDate until, CancellationToken ct)
        => perDeadline.EventsAsync(reader, scope, today, until, ct);

    public Task<IReadOnlyList<DashboardEventView>> CalendarAsync(PersonId reader, VisibilityScope scope, LocalDate today, LocalDate until, CancellationToken ct)
        => calendar.EventsAsync(reader, scope, today, until, ct);

    public async Task<bool> DeclaresAloneAsync(PersonId person, CancellationToken ct)
        => (await assessments.ListForAsync(person, null, ct)).Any(n => n.Assessment.Declarants.Count == 1);

    public Task<bool> DeclaresAsync(PersonId person, CancellationToken ct) => assessments.DeclaresAsync(person, ct);
}
