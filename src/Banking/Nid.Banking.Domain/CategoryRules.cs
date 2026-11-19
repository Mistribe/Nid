using Nid.Banking.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Banking.Domain;

/// <summary>What a category rule reads and which one wins when several match a line.</summary>
public static class CategoryRules
{
    public const int MinPattern = 2, MaxPattern = 60;

    public static Result<string> NormalizePattern(string raw)
    {
        var pattern = TransactionLabel.Normalize(raw + " "); // a bare « cb » is the bank prefix, not a pattern
        return pattern.Length is >= MinPattern and <= MaxPattern
            ? Result.Success(pattern)
            : Result.Failure<string>(new Failure(BankingCodes.RulePattern, $"A pattern is {MinPattern} to {MaxPattern} characters long.", null));
    }

    public static bool Matches(CategoryRule rule, Guid account, decimal amount, string normalizedLabel, string? normalizedCounterparty)
    {
        if (rule.Account is { } only && only != account) return false;
        if (rule.Direction is { } direction && (direction == RuleDirection.Debit) != amount < 0) return false;
        var size = Math.Abs(amount);
        if (size < rule.MinAmount || size > rule.MaxAmount) return false;
        return normalizedLabel.Contains(rule.Pattern, StringComparison.Ordinal)
               || normalizedCounterparty?.Contains(rule.Pattern, StringComparison.Ordinal) == true;
    }

    /// <summary>The restricted rule, then the bounded one, then the longer pattern, then the newest, then the smallest id.</summary>
    public static CategoryRule? Pick(IEnumerable<CategoryRule> rules, Guid account, decimal amount, string normalizedLabel, string? normalizedCounterparty)
        => rules.Where(r => Matches(r, account, amount, normalizedLabel, normalizedCounterparty))
            .OrderByDescending(r => r.Account is not null)
            .ThenByDescending(r => r.MinAmount is not null || r.MaxAmount is not null)
            .ThenByDescending(r => r.Pattern.Length)
            .ThenByDescending(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .FirstOrDefault();
}
