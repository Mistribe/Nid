using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Investments.Application;

/// <summary>An amount on the wire: a decimal string in invariant culture and its currency (same shape as Subscriptions' MoneyView).</summary>
public sealed record AmountView(string Amount, string Currency)
{
    public static AmountView From(Money money) => new(money.AmountText, money.Currency.Code);

    public static AmountView? From(Money? money) => money is { } m ? From(m) : null;
}

public sealed record FiguresView(
    AmountView? Value, LocalDate? AsOf, AmountView? NetContributions, AmountView? Gain, decimal? GainPct,
    AmountView? Fees, AmountView? GainAfterFees, AmountView? Income,
    AmountView ContributionsSince, AmountView WithdrawalsSince, bool MissingOpening, int? StaleDays)
{
    public static FiguresView From(EnvelopeFigures f) => new(
        AmountView.From(f.Value), f.AsOf, AmountView.From(f.NetContributions), AmountView.From(f.Gain), f.GainPct,
        AmountView.From(f.Fees), AmountView.From(f.GainAfterFees), AmountView.From(f.Income),
        AmountView.From(f.ContributionsSince), AmountView.From(f.WithdrawalsSince), f.MissingOpening, f.StaleDays);
}

public sealed record EnvelopeSummary(
    ItemId Id, string DisplayName, string Kind, string Currency, string Visibility, bool Closed, bool CanWrite, FiguresView Figures,
    IReadOnlyList<ItemOwnerView> Owners);

public sealed record EnvelopeList(IReadOnlyList<EnvelopeSummary> Items) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.Select(i => i.Id);
}

public sealed record ValuationView(Guid Id, LocalDate AsOf, AmountView Amount, string? Note);

public sealed record FlowView(Guid Id, LocalDate OccurredOn, string Kind, AmountView Amount, string? Category, string? ExternalRef, string? Note);

public sealed record EnvelopeDetail(
    ItemId Id, string DisplayName, string Kind, string Currency, string Visibility, bool Closed, bool CanWrite,
    string? Institution, string? AccountHint, LocalDate? OpenedOn, string ValuationCadence, int? StaleAfterDays, string? Note,
    FiguresView Figures, IReadOnlyList<ValuationView> Valuations, IReadOnlyList<FlowView> Flows) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [Id];
}

public sealed record PeriodView(
    LocalDate From, LocalDate To, LocalDate? StartAsOf, AmountView? StartValue, LocalDate? EndAsOf, AmountView? EndValue,
    AmountView? NetContributions, AmountView? Performance)
{
    // Named Of, not From: a positional property already named From collides with a same-named static method (CS0102).
    public static PeriodView Of(PeriodPerformance p) => new(p.From, p.To, p.StartAsOf, AmountView.From(p.StartValue),
        p.EndAsOf, AmountView.From(p.EndValue), AmountView.From(p.NetContributions), AmountView.From(p.Performance));
}

public sealed record SeriesPoint(LocalDate AsOf, AmountView Value, AmountView NetContributions);

public sealed record EnvelopeSeries(ItemId Id, IReadOnlyList<SeriesPoint> Points) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [Id];
}
