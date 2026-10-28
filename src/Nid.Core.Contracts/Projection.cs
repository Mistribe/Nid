namespace Nid.Core.Contracts;

/// <summary>
/// Net worth projection spec §2.1: the household's assumptions. Rates and inflation are annual fractions (<c>0.06</c> is 6 %);
/// <c>MonthlySavings</c> is in the reference currency, <c>null</c> meaning automatic.
/// </summary>
public sealed record ProjectionAssumptions(
    IReadOnlyDictionary<string, decimal> ClassRates, decimal Inflation, decimal? MonthlySavings, string SavingsClass, bool FreedPaymentsToSavings);

/// <summary>What a household that never chose assumptions gets; the property purchase scenario starts from the same rates.</summary>
public static class ProjectionDefaults
{
    /// <summary>The pre-filled annual rates per class, as fractions.</summary>
    public static readonly IReadOnlyDictionary<string, decimal> ClassRates = new Dictionary<string, decimal>
    {
        [AllocationClasses.Cash] = 0.02m,
        [AllocationClasses.MoneyMarket] = 0.025m,
        [AllocationClasses.Bond] = 0.03m,
        [AllocationClasses.Equity] = 0.06m,
        [AllocationClasses.Mixed] = 0.04m,
        [AllocationClasses.CapitalGuaranteed] = 0.025m,
        [AllocationClasses.RealEstate] = 0.02m,
        [AllocationClasses.Other] = 0m,
    };

    public static readonly ProjectionAssumptions Assumptions = new(ClassRates, 0.02m, null, AllocationClasses.Cash, true);
}
