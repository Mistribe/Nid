using System.Text.RegularExpressions;
using Nid.Core.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Domain;

/// <summary>2A spec §5: the bounds of a fact that need nothing but the fact. The stores check access, currency and date first.</summary>
public static partial class FactRules
{
    public const int NoteMax = 2000;
    public const int CategoryMax = 64;
    public const int ExternalRefMax = 200;

    /// <summary>numeric(19,4) keeps 15 integer digits: a larger amount would fail in the database.</summary>
    public const decimal AmountBound = 1_000_000_000_000_000m;

    [GeneratedRegex("^[a-z0-9][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Category();

    public static Result CheckValuation(NewValuation valuation)
        => First(Bound(valuation.Value.Amount), Note(valuation.Note));

    public static Result CheckFlow(NewFlow flow)
    {
        if (flow.Amount.Amount <= 0)
            return Result.Failure(new Failure(CoreFactCodes.AmountNotPositive, "A flow's amount is positive; its kind gives the direction.", null));

        return First(
            Bound(flow.Amount.Amount),
            flow.Category is { } category && !Category().IsMatch(category)
                ? Invalid("category", $"A category is a lowercase slug of at most {CategoryMax} characters.")
                : Result.Success(),
            // Refused, never trimmed: "A1" and "A1 " can never be two references, and what is stored is what was sent.
            flow.ExternalRef is { } reference && (reference.Length is 0 or > ExternalRefMax || reference.Any(char.IsControl)
                || char.IsWhiteSpace(reference[0]) || char.IsWhiteSpace(reference[^1]))
                ? Invalid("external_ref",
                    $"An external reference has 1 to {ExternalRefMax} characters, no control character, and no space at either end.")
                : Result.Success(),
            Note(flow.Note));
    }

    private static Result Bound(decimal amount)
        => Math.Abs(amount) >= AmountBound ? Invalid("amount", "The amount is out of range.") : Result.Success();

    private static Result Note(string? note)
        => note is { Length: > NoteMax } ? Invalid("note", $"A note has at most {NoteMax} characters.") : Result.Success();

    private static Result First(params Result[] results)
    {
        foreach (var r in results)
            if (!r.IsSuccess)
                return r;
        return Result.Success();
    }

    private static Result Invalid(string field, string message)
        => Result.Failure(new Failure(CoreFactCodes.InvalidFact, message, new Dictionary<string, object?> { ["field"] = field }));
}
