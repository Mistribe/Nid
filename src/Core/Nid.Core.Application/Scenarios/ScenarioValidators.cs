using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Scenarios;

/// <summary>
/// #515: the bounds of a scenario document's amounts and rates, checked in the pipeline whoever sends it (REST, MCP). The wire
/// paths and codes are the ones the endpoints answered; the document's own rules (<c>ScenarioRules.Check</c>) still run in the
/// handler. Rates are fractions here (the wire sends percents).
/// </summary>
internal static class ScenarioChecks
{
    private const decimal AmountCeiling = 1_000_000_000_000m;
    private const decimal RateCeiling = 0.20m;
    private const decimal GrowthFloor = -0.20m;

    public static Result Check(PropertyPurchase doc)
    {
        var errors = new List<FieldError>();
        for (var i = 0; i < doc.Funding.Count; i++)
            Amount(errors, doc.Funding[i].Amount, $"funding[{i}].amount");
        for (var i = 0; i < doc.Loans.Count; i++)
        {
            var loan = doc.Loans[i];
            var at = $"loans[{i}]";
            if (loan.Insurance.Kind == ScenarioInsuranceKind.RateOnInitial) Rate(errors, loan.Insurance.Value, $"{at}.insurance.value", 0m);
            else Amount(errors, loan.Insurance.Value, $"{at}.insurance.value");
            Amount(errors, loan.Amount, $"{at}.amount");
            Rate(errors, loan.Rate, $"{at}.rate", 0m);
            Amount(errors, loan.FileFees, $"{at}.fileFees");
            Amount(errors, loan.GuaranteeFees, $"{at}.guaranteeFees");
        }

        var a = doc.Assumptions;
        foreach (var (key, rate) in a.ClassRates)
            Rate(errors, rate, $"assumptions.classRates.{key}", GrowthFloor);
        if (a.PropertyGrowth is { } growth) Rate(errors, growth, "assumptions.propertyGrowth", GrowthFloor);
        Amount(errors, a.MonthlySavings, "assumptions.monthlySavings");

        Amount(errors, doc.Price, "price");
        Amount(errors, doc.NotaryFees, "notaryFees");
        Amount(errors, doc.AgencyFees, "agencyFees");
        Amount(errors, doc.OtherCosts, "otherCosts");
        Amount(errors, doc.MonthlyCharges, "monthlyCharges");
        Amount(errors, doc.CurrentRent, "currentRent");
        Amount(errors, doc.ExpectedRent, "expectedRent");
        return Answer(errors);
    }

    public static Result Check(SaleScenario doc)
    {
        var errors = new List<FieldError>();
        Amount(errors, doc.Price, "price");
        Amount(errors, doc.AgencyFees, "agencyFees");
        Amount(errors, doc.Penalties, "penalties");
        return Answer(errors);
    }

    private static void Amount(List<FieldError> errors, decimal value, string path)
    {
        if (value is < 0 or >= AmountCeiling)
            errors.Add(new FieldError(path, "request.amount", "An amount is not negative and below 10^12."));
    }

    private static void Rate(List<FieldError> errors, decimal value, string path, decimal min)
    {
        if (value < min || value > RateCeiling)
            errors.Add(new FieldError(path, "request.rate", $"A rate is a percent from {min * 100:0} to 20."));
    }

    private static Result Answer(List<FieldError> errors)
        => errors.Count == 0 ? Result.Success() : Result.Failure(FieldErrors.Shape(errors));
}

[Validator]
internal sealed class CreateScenarioValidator : IRequestValidator<CreateScenarioCommand>
{
    public ValueTask<Result> ValidateAsync(CreateScenarioCommand request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ScenarioChecks.Check(request.Document));
}

[Validator]
internal sealed class ReplaceScenarioValidator : IRequestValidator<ReplaceScenarioCommand>
{
    public ValueTask<Result> ValidateAsync(ReplaceScenarioCommand request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ScenarioChecks.Check(request.Document));
}

[Validator]
internal sealed class SimulateScenarioValidator : IRequestValidator<SimulateScenarioQuery>
{
    public ValueTask<Result> ValidateAsync(SimulateScenarioQuery request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ScenarioChecks.Check(request.Document));
}

[Validator]
internal sealed class CreateSaleScenarioValidator : IRequestValidator<CreateSaleScenarioCommand>
{
    public ValueTask<Result> ValidateAsync(CreateSaleScenarioCommand request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ScenarioChecks.Check(request.Document));
}

[Validator]
internal sealed class ReplaceSaleScenarioValidator : IRequestValidator<ReplaceSaleScenarioCommand>
{
    public ValueTask<Result> ValidateAsync(ReplaceSaleScenarioCommand request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ScenarioChecks.Check(request.Document));
}

[Validator]
internal sealed class SimulateSaleScenarioValidator : IRequestValidator<SimulateSaleScenarioQuery>
{
    public ValueTask<Result> ValidateAsync(SimulateSaleScenarioQuery request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ScenarioChecks.Check(request.Document));
}
