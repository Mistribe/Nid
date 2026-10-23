using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

/// <summary>Property purchase scenario spec §3: the kinds of scenario; rates are fractions, amounts are in the reference currency.</summary>
public static class ScenarioKinds
{
    public const string PropertyPurchase = "property_purchase";
}

public enum PurchaseUse { Primary, Rental }

public enum DeferralKind { Partial, Total }

public enum ScenarioInsuranceKind { RateOnInitial, FixedMonthly }

public sealed record ScenarioDeferral(DeferralKind Kind, int Months);

/// <summary><c>Value</c> is an annual rate for <c>RateOnInitial</c>, a monthly amount for <c>FixedMonthly</c>.</summary>
public sealed record ScenarioInsurance(ScenarioInsuranceKind Kind, decimal Value);

public sealed record ScenarioLoan(
    string Name, decimal Amount, decimal Rate, int Months, ScenarioDeferral? Deferral, ScenarioInsurance Insurance,
    decimal FileFees, decimal GuaranteeFees);

public sealed record FundingLine(ItemId Item, decimal Amount);

public sealed record ScenarioAssumptions(
    int HorizonYears, IReadOnlyDictionary<string, decimal> ClassRates, decimal? PropertyGrowth, decimal MonthlySavings, string SavingsClass);

/// <summary>The document of a property purchase scenario. <c>RentalRegime</c> is <c>"micro"</c>, <c>"real"</c> or null (the tax profile's).</summary>
public sealed record PropertyPurchase(
    int Version, PurchaseUse Use, LocalDate PurchaseOn, decimal Price, decimal NotaryFees, decimal AgencyFees, decimal OtherCosts,
    decimal MonthlyCharges, decimal CurrentRent, decimal ExpectedRent, int VacancyMonths, string? RentalRegime,
    IReadOnlyList<FundingLine> Funding, IReadOnlyList<ScenarioLoan> Loans, ScenarioAssumptions Assumptions);

public sealed record SimulatedPayment(int Number, LocalDate DueDate, decimal Payment, decimal Interest, decimal Principal, decimal Insurance, decimal Balance);

/// <summary>A loan of the scenario read by the real engine; <c>Rows</c> in due order.</summary>
public sealed record SimulatedLoan(string Name, IReadOnlyList<SimulatedPayment> Rows, decimal Fees);

public interface ILoanSimulator
{
    /// <summary>Spec §3: disbursed on <paramref name="purchaseOn"/>, first payment a month later; a refusal names <c>loans[i]</c>.</summary>
    Result<IReadOnlyList<SimulatedLoan>> Simulate(Currency currency, LocalDate purchaseOn, IReadOnlyList<ScenarioLoan> loans);
}

/// <summary>Spec §4.2 step 3: month 0 is this month at today's figures; amounts converted to the reference at today's rate.</summary>
public sealed record RealEstateMonth(YearMonth Month, decimal PropertyValue, decimal Debt, decimal LoanPayments);

public sealed record RealEstateProjection(IReadOnlyList<RealEstateMonth> Months, IReadOnlyList<string> MissingCurrencies);

public interface IRealEstateProjection
{
    Task<Result<RealEstateProjection>> ProjectAsync(
        PersonId reader, VisibilityScope scope, LocalDate today, int months, decimal annualGrowth, CancellationToken ct);
}
