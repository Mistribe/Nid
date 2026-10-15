using UnambitiousFx.Functional.Failures;

namespace Nid.Platform.Contracts;

/// <summary>One refused field of a request body; <paramref name="Path"/> is its camelCase JSON path (1E2a design §2.1).</summary>
public sealed record FieldError(string Path, string Code, string Message);

/// <summary>
/// 1E2a design §2.1. A body whose shape is wrong is one <see cref="ShapeCode"/> failure listing every field; a rule that
/// refuses keeps its own code and says which field it concerns. The host turns both into 422 with an <c>errors</c> list.
/// </summary>
public static class FieldErrors
{
    public const string ShapeCode = "request.invalid";
    private const string Key = "errors";

    public static Failure Shape(IReadOnlyList<FieldError> errors)
        => new(ShapeCode, errors.Count == 1 ? errors[0].Message : "Some fields are not valid.",
            new Dictionary<string, object?> { [Key] = errors });

    /// <summary>The same failure, located. A typed failure (not found, forbidden) is not a field's and stays as it is.</summary>
    public static Failure At(Failure failure, string path)
        => failure.GetType() != typeof(Failure)
            ? failure
            : new Failure(failure.Code, failure.Message,
                new Dictionary<string, object?> { [Key] = new List<FieldError> { new(path, failure.Code, failure.Message) } });

    /// <summary>One rule refusing several fields at once (every unreadable cell of a pasted table, spec 3F2 §3), under its own code.</summary>
    public static Failure Many(string code, string message, IReadOnlyList<FieldError> errors)
        => new(code, message, new Dictionary<string, object?> { [Key] = errors });

    public static IReadOnlyList<FieldError>? Of(Failure failure)
        => failure.Metadata is { } metadata && metadata.TryGetValue(Key, out var errors) ? errors as IReadOnlyList<FieldError> : null;
}
