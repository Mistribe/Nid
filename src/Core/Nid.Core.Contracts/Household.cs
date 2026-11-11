using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

/// <summary><paramref name="AdultCount"/> counts active and pending adults: both count against the cap.</summary>
public sealed record HouseholdSettingsInfo(
    int MaxAdults, Currency ReferenceCurrency, int AdultCount, bool PricesEnabled, bool IrlEnabled = false, bool BankingEnabled = false,
    string? EnableBankingAppId = null, int? BankRetentionMonths = null, bool InflationEnabled = true);

/// <summary>
/// A field left <c>null</c> is left unchanged; an empty <paramref name="EnableBankingAppId"/> clears it;
/// <paramref name="ClearBankRetention"/> clears <paramref name="BankRetentionMonths"/> (1 to 600; none keeps every transaction).
/// </summary>
public sealed record HouseholdSettingsChange(
    int? MaxAdults, string? ReferenceCurrency, bool? PricesEnabled = null, bool? IrlEnabled = null, bool? BankingEnabled = null,
    string? EnableBankingAppId = null, int? BankRetentionMonths = null, bool ClearBankRetention = false, bool? InflationEnabled = null);

/// <summary>
/// Issue #209: the household's fee references, in %: contract fees by envelope kind, support fees by holding kind (wire values), and
/// the assumed gross return their cost is projected at.
/// </summary>
public sealed record FeeReferences(decimal AssumedReturnPct, IReadOnlyDictionary<string, decimal> Envelopes, IReadOnlyDictionary<string, decimal> Holdings);

public interface IHouseholdSettings
{
    /// <summary>The currency of household totals; EUR while the settings row does not exist yet (it is created at the first login).</summary>
    Task<Currency> ReferenceCurrencyAsync(CancellationToken ct);

    Task<HouseholdSettingsInfo> ReadAsync(CancellationToken ct);

    /// <summary>
    /// The cap is at least 1 and at least the number of adults (<see cref="PersonCodes.CapBelowCount"/>); the currency is a
    /// three-letter upper-case code (<see cref="PersonCodes.UnknownCurrency"/>).
    /// </summary>
    Task<Result<HouseholdSettingsInfo>> UpdateAsync(HouseholdSettingsChange change, CancellationToken ct);

    /// <summary>Issue #104: the household's target share of each asset class, in % (empty: no target).</summary>
    Task<IReadOnlyDictionary<string, decimal>> AllocationTargetAsync(CancellationToken ct);

    /// <summary>Issue #104: replaces the target; checked by the caller (<c>AllocationTarget.Check</c>).</summary>
    Task SaveAllocationTargetAsync(IReadOnlyDictionary<string, decimal> target, CancellationToken ct);

    /// <summary>Net worth projection spec §2.1: the household's assumptions; <see cref="ProjectionDefaults.Assumptions"/> until it chose some.</summary>
    Task<ProjectionAssumptions> ProjectionAssumptionsAsync(CancellationToken ct);

    /// <summary>Replaces the assumptions; checked by the caller (<c>ProjectionRules.Check</c>).</summary>
    Task SaveProjectionAssumptionsAsync(ProjectionAssumptions assumptions, CancellationToken ct);

    /// <summary>
    /// #155: reads the assumptions, applies <paramref name="change"/> (which checks the result) and stores what it gives, under one lock,
    /// so two partial updates at once never lose one another; a failure stores nothing.
    /// </summary>
    Task<Result<ProjectionAssumptions>> UpdateProjectionAssumptionsAsync(Func<ProjectionAssumptions, Result<ProjectionAssumptions>> change, CancellationToken ct);

    /// <summary>Issue #209: the fee references the household saved; null until it saved some (Investments holds the defaults).</summary>
    Task<FeeReferences?> FeeReferencesAsync(CancellationToken ct);

    /// <summary>Replaces the fee references; checked by the caller.</summary>
    Task SaveFeeReferencesAsync(FeeReferences references, CancellationToken ct);
}
