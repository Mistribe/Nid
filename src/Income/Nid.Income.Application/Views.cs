using System.Globalization;
using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Income.Domain;
using NodaTime;
using Duration = Nid.Core.Contracts.Duration;

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
    IncomeAmountView MonthlyEquivalent, int RunningPhases, IReadOnlyList<string> GapSoon, bool CanWrite, bool Ended, LocalDate? EndedOn,
    IReadOnlyList<ItemOwnerView> Owners);

public sealed record IncomeSourceList(IReadOnlyList<IncomeSourceSummary> Items) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.Select(i => i.Id);
}

/// <summary>An entered amount on the wire, exact (<see cref="Money.AmountText"/>): a phase's, a one-off's, a month's total.</summary>
public sealed record IncomeMoneyView(string Amount, string Currency)
{
    public static IncomeMoneyView From(Money m) => new(m.AmountText, m.Currency.Code);
}

public sealed record IncomeDurationView(int Count, string Unit)
{
    public static IncomeDurationView From(Duration d) => new(d.Count, d.Unit.ToString().ToLowerInvariant());
}

/// <summary>A phase with its <c>status</c> today (<c>past</c>, <c>running</c>, <c>upcoming</c>) and the <c>version</c> a correction sends back.</summary>
public sealed record IncomePhaseView(
    Guid Id, string Label, LocalDate StartsOn, LocalDate? EndsOn, IncomeMoneyView NetAmount, IncomeDurationView Every,
    IncomeMoneyView? GrossAmount, int? MaxCycles, string? Note, string Status, IncomeAmountView MonthlyEquivalent, uint Version,
    IncomeMoneyView? TaxableAmount = null)
{
    internal static IncomePhaseView Of(IncomePhase phase, LocalDate today, Currency currency)
        => new(phase.Id, phase.Label, phase.StartsOn, phase.EndsOn, IncomeMoneyView.From(phase.NetAmount), IncomeDurationView.From(phase.Every),
            phase.GrossAmount is { } gross ? IncomeMoneyView.From(gross) : null, phase.MaxCycles, phase.Note,
            IncomeMath.EffectiveEnd(phase) is { } end && end <= today ? "past" : phase.StartsOn > today ? "upcoming" : "running",
            IncomeAmountView.Of(IncomeMath.MonthlyEquivalent(phase), currency), phase.Version,
            phase.TaxableAmount is { } taxable ? IncomeMoneyView.From(taxable) : null);
}

public sealed record IncomeGapView(string Label, LocalDate EndsOn);

/// <summary>
/// A one-off income: a Core flow <c>income</c> of category <c>one_off</c> on the source's item, with the net taxable
/// income kept for it by <see cref="IOneOffTaxableStore"/> when it has one.
/// </summary>
public sealed record IncomeOneOffView(Guid Id, LocalDate OccurredOn, IncomeMoneyView Amount, string? Note, IncomeMoneyView? TaxableAmount = null)
{
    internal static IncomeOneOffView Of(Flow flow, IReadOnlyDictionary<Guid, decimal> taxables)
        => new(flow.Id, flow.OccurredOn, IncomeMoneyView.From(flow.Amount), flow.Note,
            taxables.TryGetValue(flow.Id, out var taxable) && Money.Create(taxable, flow.Amount.Currency).TryGetValue(out var money)
                ? IncomeMoneyView.From(money)
                : null);
}

/// <summary>A source in full: its phases, its gap warnings and the one-offs of the last 12 months.</summary>
public sealed record IncomeSourceDetail(
    ItemId Id, string DisplayName, string Kind, string? PayerLabel, string? Note, string Currency, string Visibility,
    bool CanWrite, bool Ended, LocalDate? EndedOn, IncomeAmountView MonthlyEquivalent, IReadOnlyList<IncomePhaseView> Phases,
    IReadOnlyList<IncomeGapView> GapSoon, IReadOnlyList<IncomeOneOffView> OneOffs) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [Id];
}

public sealed record IncomeOccurrenceView(Guid PhaseId, string Label, LocalDate On, IncomeMoneyView Amount);

/// <summary>A calendar month of expected income; its total is exact, the sum of the entered amounts.</summary>
public sealed record IncomeExpectedMonth(
    int Year, int Month, IReadOnlyList<IncomeOccurrenceView> Occurrences, IReadOnlyList<IncomeOneOffView> OneOffs, IncomeMoneyView Total);

public sealed record IncomeExpected(ItemId Id, LocalDate From, LocalDate To, IReadOnlyList<IncomeExpectedMonth> Months) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [Id];
}
