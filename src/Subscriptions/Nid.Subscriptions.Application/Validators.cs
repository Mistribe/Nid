using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Subscriptions.Application;

/// <summary>
/// #515: a phase's own bounds, checked in the pipeline before any write, whoever sends the command (REST, MCP, a replayed
/// proposal). The engine only sees a phase inside a timeline; the database constraint would answer too late.
/// </summary>
internal static class PhaseChecks
{
    public static void Check(List<FieldError> errors, Money price, int? maxCycles, string prefix)
    {
        if (price.Amount < 0)
            errors.Add(new FieldError(prefix + "price.amount", "subscriptions.price_negative", "A phase has a price of zero or more."));
        if (maxCycles is <= 0)
            errors.Add(new FieldError(prefix + "maxCycles", "subscriptions.max_cycles", "A cycle limit is positive."));
    }

    public static Result Answer(List<FieldError> errors)
        => errors.Count == 0 ? Result.Success() : Result.Failure(FieldErrors.Shape(errors));
}

[Validator]
internal sealed class CreateSubscriptionValidator : IRequestValidator<CreateSubscriptionCommand>
{
    public ValueTask<Result> ValidateAsync(CreateSubscriptionCommand request, CancellationToken cancellationToken = default)
    {
        var errors = new List<FieldError>();
        var phases = request.Phases ?? [];
        for (var i = 0; i < phases.Count; i++)
            PhaseChecks.Check(errors, phases[i].Price, phases[i].MaxCycles, $"phases[{i}].");
        return ValueTask.FromResult(PhaseChecks.Answer(errors));
    }
}

[Validator]
internal sealed class OpenPhaseValidator : IRequestValidator<OpenPhaseCommand>
{
    public ValueTask<Result> ValidateAsync(OpenPhaseCommand request, CancellationToken cancellationToken = default)
    {
        var errors = new List<FieldError>();
        PhaseChecks.Check(errors, request.Phase.Price, request.Phase.MaxCycles, "");
        return ValueTask.FromResult(PhaseChecks.Answer(errors));
    }
}

[Validator]
internal sealed class ChangePriceValidator : IRequestValidator<ChangePriceCommand>
{
    public ValueTask<Result> ValidateAsync(ChangePriceCommand request, CancellationToken cancellationToken = default)
    {
        var errors = new List<FieldError>();
        PhaseChecks.Check(errors, request.Price, request.MaxCycles, "");
        return ValueTask.FromResult(PhaseChecks.Answer(errors));
    }
}
