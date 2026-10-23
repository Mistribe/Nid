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

    /// <summary>A loan as 3A2 stores it (spec 3A2 §2): the golden case unless <paramref name="terms"/> says otherwise.</summary>
    public static StoredLoan Stored(
        IReadOnlyList<StoredFee>? fees = null, IReadOnlyList<StoredRepayment>? repayments = null, LoanTerms? terms = null)
    {
        var t = terms ?? Golden();
        return new StoredLoan(ItemId.New(), "Banque", null,
            new LoanContract(t.Principal, t.DisbursedOn, t.FirstPaymentDate, t.TotalPayments, t.Rounding, t.FirstPeriod,
                t.Phases.Select(p => new LoanPhaseEntry(p, null)).ToList(), t.Insurances.Select(i => new LoanInsuranceEntry(i, null)).ToList()),
            7, fees ?? [], repayments ?? []);
    }

    /// <summary>The loan's Core item: shared, one adult borrower, in euros.</summary>
    public static ItemAccess Item()
        => new(ItemId.New(), "Prêt maison", Visibility.Shared, [new Owner(PersonId.New(), PersonKind.Adult, 10_000)]) { Currency = "EUR" };

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

    /// <summary>The tenancies as one revision chain, each revising the one before: a single tenant's rent account.</summary>
    public static IReadOnlyList<(Guid Id, Guid? RevisedFrom, Tenancy Tenancy)> Chain(IEnumerable<Tenancy> tenancies)
    {
        var links = new List<(Guid Id, Guid? RevisedFrom, Tenancy Tenancy)>();
        foreach (var t in tenancies)
            links.Add((Guid.NewGuid(), links.Count == 0 ? null : links[^1].Id, t));
        return links;
    }
}
