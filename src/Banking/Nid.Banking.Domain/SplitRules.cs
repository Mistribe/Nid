using Nid.Banking.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Banking.Domain;

/// <summary>#84: what a split of a bank line asks.</summary>
public static class SplitRules
{
    public const int MinParts = 2;
    public const int MaxParts = 20;
    public const int MaxNote = 500;

    /// <summary>2 to 20 parts, each non-zero, of the line's sign, to the 4 decimals stored; then they add up to <paramref name="lineAmount"/> exactly.</summary>
    public static Result Validate(decimal lineAmount, IReadOnlyList<PartInput> parts)
    {
        if (parts.Count is < MinParts or > MaxParts)
            return Fail(BankingCodes.SplitPart, $"A split has {MinParts} to {MaxParts} parts.");
        if (parts.Any(p => p.Amount == 0m || Math.Sign(p.Amount) != Math.Sign(lineAmount) || decimal.Round(p.Amount, 4) != p.Amount
            || (p.Note?.Trim().Length ?? 0) > MaxNote))
            return Fail(BankingCodes.SplitPart, $"A part is not zero, has the line's sign, 4 decimals at most and a note of {MaxNote} characters at most.");
        return parts.Sum(p => p.Amount) == lineAmount ? Result.Success() : Fail(BankingCodes.SplitSum, "The parts add up to the line's amount.");
    }

    private static Result Fail(string code, string message) => Result.Failure(new Failure(code, message, new Dictionary<string, object?> { ["field"] = "parts" }));
}
