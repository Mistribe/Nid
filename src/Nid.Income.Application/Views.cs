using System.Globalization;
using Nid.Core.Contracts;
using Nid.Income.Domain;
using NodaTime;

namespace Nid.Income.Application;

/// <summary>
/// A computed amount on the wire, rounded to 0.01 half up (spec §3): a monthly equivalent. Built from a full-precision
/// decimal — a quarterly 1 000 € is 333.333…, beyond <see cref="Money"/>'s four decimals — and the source's currency.
/// </summary>
public sealed record IncomeAmountView(string Amount, string Currency)
{
    public static IncomeAmountView Of(decimal amount, Currency currency)
        => new(IncomeMath.Round(amount).ToString("0.00", CultureInfo.InvariantCulture), currency.Code);
}

public sealed record IncomeSourceSummary(
    ItemId Id, string DisplayName, string Kind, string? PayerLabel, string Currency, string Visibility,
    IncomeAmountView MonthlyEquivalent, int RunningPhases, IReadOnlyList<string> GapSoon, bool CanWrite, bool Ended, LocalDate? EndedOn);

public sealed record IncomeSourceList(IReadOnlyList<IncomeSourceSummary> Items) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.Select(i => i.Id);
}

/// <summary>A source in full; Task 3 adds its phases, its gap warnings and its one-offs after these members.</summary>
public sealed record IncomeSourceDetail(
    ItemId Id, string DisplayName, string Kind, string? PayerLabel, string? Note, string Currency, string Visibility,
    bool CanWrite, bool Ended, LocalDate? EndedOn, IncomeAmountView MonthlyEquivalent) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [Id];
}
