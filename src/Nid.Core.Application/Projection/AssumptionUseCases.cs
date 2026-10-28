using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Projection;

public static class ProjectionCodes
{
    public const string InvalidField = "projection.invalid_field";

    /// <summary>Spec §2.2: a retirement horizon names a person with no birth date, or no person the reader knows.</summary>
    public const string NoBirthDate = "projection.no_birth_date";

    /// <summary>Spec §2.3: a retirement already reached, or more than 40 years away.</summary>
    public const string InvalidHorizon = "projection.invalid_horizon";
}

/// <summary>Net worth projection spec §2.1: the limits of the household's assumptions. Fields are the camelCase wire paths.</summary>
public static class ProjectionRules
{
    public const decimal MaxRate = 0.5m;
    public const decimal MinInflation = -0.05m;
    public const decimal MaxInflation = 0.2m;

    /// <summary>Every refusal; empty when valid.</summary>
    public static IReadOnlyList<(string Field, string Message)> Check(ProjectionAssumptions a)
    {
        var errors = new List<(string, string)>();
        foreach (var @class in AllocationClasses.All.Where(c => !a.ClassRates.ContainsKey(c)))
            errors.Add(($"classRates.{@class}", "A rate is required for every asset class."));
        foreach (var (@class, rate) in a.ClassRates)
        {
            if (!AllocationClasses.All.Contains(@class))
                errors.Add(($"classRates.{@class}", $"Not an asset class: use {string.Join(", ", AllocationClasses.All)}."));
            else if (Math.Abs(rate) > MaxRate)
                errors.Add(($"classRates.{@class}", "A rate is from -50 % to 50 %."));
        }
        if (a.Inflation is < MinInflation or > MaxInflation) errors.Add(("inflation", "Inflation is from -5 % to 20 %."));
        if (!AllocationClasses.All.Contains(a.SavingsClass))
            errors.Add(("savingsClass", $"Not an asset class: use {string.Join(", ", AllocationClasses.All)}."));
        return errors;
    }
}

/// <summary>Percent strings for the rates (<c>"6"</c>), the savings in the reference currency (<c>"500.00"</c>, <c>null</c>: automatic); <c>Currency</c> is the household reference currency code the savings are in.</summary>
public sealed record ProjectionAssumptionsView(
    IReadOnlyDictionary<string, string> ClassRates, string Inflation, string? MonthlySavings, string SavingsClass, bool FreedPaymentsToSavings, string Currency)
{
    public static ProjectionAssumptionsView From(ProjectionAssumptions a, string currency) => new(
        a.ClassRates.ToDictionary(r => r.Key, r => Percent(r.Value)), Percent(a.Inflation),
        a.MonthlySavings is { } savings ? Scenarios.PropertyPurchaseView.Amount(savings) : null, a.SavingsClass, a.FreedPaymentsToSavings, currency);

    private static string Percent(decimal fraction) => Scenarios.PropertyPurchaseView.Rate(fraction);
}

public sealed record GetProjectionAssumptionsQuery : IQuery<ProjectionAssumptionsView>;

/// <summary>Spec §2.1: replaces the household's assumptions whole; any adult may.</summary>
public sealed record SaveProjectionAssumptionsCommand(ProjectionAssumptions Assumptions) : ICommand<ProjectionAssumptionsView>;

[RequestHandler<GetProjectionAssumptionsQuery, ProjectionAssumptionsView>]
public sealed class GetProjectionAssumptionsHandler(IHouseholdSettings household) : IRequestHandler<GetProjectionAssumptionsQuery, ProjectionAssumptionsView>
{
    public async ValueTask<Result<ProjectionAssumptionsView>> HandleAsync(GetProjectionAssumptionsQuery query, CancellationToken ct = default)
        => Result.Success(ProjectionAssumptionsView.From(await household.ProjectionAssumptionsAsync(ct), (await household.ReferenceCurrencyAsync(ct)).Code));
}

[RequestHandler<SaveProjectionAssumptionsCommand, ProjectionAssumptionsView>]
public sealed class SaveProjectionAssumptionsHandler(IHouseholdSettings household) : IRequestHandler<SaveProjectionAssumptionsCommand, ProjectionAssumptionsView>
{
    public async ValueTask<Result<ProjectionAssumptionsView>> HandleAsync(SaveProjectionAssumptionsCommand command, CancellationToken ct = default)
    {
        var errors = ProjectionRules.Check(command.Assumptions);
        if (errors.Count > 0)
            return Result.Failure<ProjectionAssumptionsView>(FieldErrors.Many(ProjectionCodes.InvalidField,
                errors.Count == 1 ? errors[0].Message : "Some fields are not valid.",
                errors.Select(e => new FieldError(e.Field, ProjectionCodes.InvalidField, e.Message)).ToList()));
        await household.SaveProjectionAssumptionsAsync(command.Assumptions, ct);
        return Result.Success(ProjectionAssumptionsView.From(command.Assumptions, (await household.ReferenceCurrencyAsync(ct)).Code));
    }
}
