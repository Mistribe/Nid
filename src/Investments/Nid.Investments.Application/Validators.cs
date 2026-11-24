using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Investments.Application;

/// <summary>#515: a switch moves money from at least one holding to at least one other, whoever sends it (REST, a replayed proposal).</summary>
[Validator]
internal sealed class RecordSwitchValidator : IRequestValidator<RecordSwitchCommand>
{
    public ValueTask<Result> ValidateAsync(RecordSwitchCommand request, CancellationToken cancellationToken = default)
    {
        var errors = new List<FieldError>();
        if (request.Switch.From is not { Count: > 0 })
            errors.Add(new FieldError("from", "request.required", "At least one holding the money leaves is required."));
        if (request.Switch.To is not { Count: > 0 })
            errors.Add(new FieldError("to", "request.required", "At least one holding the money goes to is required."));
        return ValueTask.FromResult(errors.Count == 0 ? Result.Success() : Result.Failure(FieldErrors.Shape(errors)));
    }
}
