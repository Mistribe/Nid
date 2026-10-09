using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.RealEstate.Tests;

internal static class Loans
{
    public static readonly LocalDate Disbursed = new(2024, 1, 15);
    public static readonly LocalDate First = new(2024, 2, 15);

    /// <summary>loans.md §4: 200 000 € at 3.5 % over 240 payments, one annuity phase, half up.</summary>
    public static LoanTerms Golden() => Annuity(200_000m, 0.035m, 240);

    public static LoanTerms Annuity(decimal principal, decimal rate, int payments, InterestRounding rounding = InterestRounding.HalfUp)
        => new(Currency.Eur, principal, Disbursed, First, payments, rounding, FirstPeriod.Full,
            [new LoanPhase(1, PhaseMode.Annuity, rate, RateType.Fixed)], [], [], []);

    public static T Ok<T>(Result<T> result) where T : notnull
    {
        Assert.True(result.TryGetValue(out var value), result.ToString());
        return value!;
    }

    public static Failure Fail<T>(Result<T> result) where T : notnull
    {
        Assert.True(result.TryGetFailure(out var failure), "Expected a refusal, got a schedule.");
        return failure!;
    }

    public static string? FieldOf(Failure failure)
        => failure.Metadata is { } m && m.TryGetValue("field", out var f) ? f as string : null;
}
