using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

/// <summary><paramref name="AdultCount"/> counts active and pending adults: both count against the cap.</summary>
public sealed record HouseholdSettingsInfo(int MaxAdults, Currency ReferenceCurrency, int AdultCount);

/// <summary>A field left <c>null</c> is left unchanged.</summary>
public sealed record HouseholdSettingsChange(int? MaxAdults, string? ReferenceCurrency);

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
}
