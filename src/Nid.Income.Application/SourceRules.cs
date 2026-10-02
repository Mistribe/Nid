using Nid.Income.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Income.Application;

/// <summary>2C1 spec §2 bounds of a source, checked before anything is written.</summary>
internal static class SourceRules
{
    public const int NameMax = 100;
    public const int PayerLabelMax = 100;
    public const int NoteMax = 2000;

    /// <summary>
    /// 1 to 100 characters after trimming, not only whitespace; stored exactly as sent (as an envelope's name). A
    /// missing or null <c>displayName</c> binds to a real null and is refused here, never dereferenced.
    /// </summary>
    public static Result Name(string? name)
        => name is null || name.Trim().Length is < 1 or > NameMax
            ? Invalid("displayName", $"A name is 1 to {NameMax} characters, and not only whitespace.")
            : Result.Success();

    /// <summary>A payer label names an employer or a fund, never a person's identifier (income-data-model.md §12).</summary>
    public static Result Check(string? payerLabel, string? note)
    {
        if (payerLabel is { Length: > PayerLabelMax })
            return Invalid("payerLabel", $"A payer label has at most {PayerLabelMax} characters.");
        if (note is { Length: > NoteMax })
            return Invalid("note", $"A note has at most {NoteMax} characters.");
        return Result.Success();
    }

    public static Result<IncomeKind> Kind(string? wire)
        => IncomeWire.TryKind(wire, out var kind) ? Result.Success(kind)
            : Result.Failure<IncomeKind>(new Failure(IncomeCodes.UnknownKind, $"'{wire}' is not an income kind.",
                new Dictionary<string, object?> { ["field"] = "kind" }));

    private static Result Invalid(string field, string message)
        => Result.Failure(new Failure(IncomeCodes.InvalidSource, message, new Dictionary<string, object?> { ["field"] = field }));
}
