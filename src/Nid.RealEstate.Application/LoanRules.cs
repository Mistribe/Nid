using Nid.RealEstate.Contracts;
using UnambitiousFx.Functional.Failures;

namespace Nid.RealEstate.Application;

/// <summary>
/// Spec 3A2 §3's rules beyond the engine's, and the bounds of the text fields (precisions 2–4). The first refusal wins;
/// null when there is none. Fields are the body's.
/// </summary>
internal static class LoanRules
{
    public const int NameMax = 100;
    public const int LenderMax = 100;
    public const int NoteMax = 2000;

    /// <summary>The spec's bound on the principal, kept for every amount: numeric(19,4) holds less than 10¹⁵.</summary>
    public const decimal AmountBound = 1_000_000_000_000m;

    /// <summary>1 to 100 characters after trimming, stored as sent (as an income source's name).</summary>
    public static Failure? Name(string? name)
        => name is null || name.Trim().Length is < 1 or > NameMax
            ? Invalid("name", $"A name is 1 to {NameMax} characters, and not only whitespace.")
            : null;

    public static Failure? Details(string? lender, string? note)
        => lender is { Length: > LenderMax } ? Invalid("lender", $"A lender has at most {LenderMax} characters.") : Note(note, "note");

    public static Failure? Note(string? note, string field)
        => note is { Length: > NoteMax } ? Invalid(field, $"A note has at most {NoteMax} characters.") : null;

    public static Failure? Amount(decimal amount, string field)
        => decimal.Round(amount, 2) != amount ? Invalid(field, "An amount has at most two decimals.")
            : Math.Abs(amount) >= AmountBound ? Invalid(field, "An amount is below 10^12.")
            : null;

    public static Failure? Amount(decimal? amount, string field) => amount is { } value ? Amount(value, field) : null;

    /// <summary>A rate is stored as numeric(9,6): a seventh decimal would be rounded away silently.</summary>
    public static Failure? Rate(decimal? rate, string field)
        => rate is { } value && decimal.Round(value, 6) != value ? Invalid(field, "A rate has at most six decimals.") : null;

    /// <summary>What the engine does not check of a contract: amounts, rates and notes.</summary>
    public static Failure? Contract(LoanContract contract)
    {
        if (Amount(contract.Principal, "principal") is { } principal)
            return principal;
        for (var i = 0; i < contract.Phases.Count; i++)
        {
            var (phase, note) = contract.Phases[i];
            if ((Rate(phase.AnnualRate, $"phases[{i}].annualRate")
                 ?? Amount(phase.PaymentOverride, $"phases[{i}].paymentOverride")
                 ?? Amount(phase.OpeningBalanceOverride, $"phases[{i}].openingBalanceOverride")
                 ?? Note(note, $"phases[{i}].note")) is { } refused)
                return refused;
        }
        for (var i = 0; i < contract.Insurances.Count; i++)
        {
            var (insurance, note) = contract.Insurances[i];
            if ((Amount(insurance.MonthlyAmount, $"insurances[{i}].monthlyAmount")
                 ?? Rate(insurance.AnnualRate, $"insurances[{i}].annualRate")
                 ?? Note(note, $"insurances[{i}].note")) is { } refused)
                return refused;
        }
        return null;
    }

    public static Failure Invalid(string field, string message)
        => new(LoanCodes.InvalidLoan, message, new Dictionary<string, object?> { ["field"] = field });
}
