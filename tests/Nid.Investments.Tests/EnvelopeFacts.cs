using Nid.Core.Contracts;
using Nid.Investments.Application;
using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Investments.Tests;

/// <summary>An envelope's facts as the overview reads them, built value by value and flow by flow; owned by <see cref="DefaultOwner"/> unless told.</summary>
internal sealed class EnvelopeFacts(string name, EnvelopeKind kind, string currency = "EUR", params (PersonId Person, int Bp)[] owners)
{
    public static readonly PersonId DefaultOwner = PersonId.New();

    private static Money M(decimal amount, Currency currency)
        => Money.Create(amount, currency).TryGetValue(out var m) ? m : throw new InvalidOperationException();

    private readonly ItemId id = ItemId.New();
    private readonly Currency money = Currency.TryCreate(currency, out var c) ? c : Currency.Eur;
    private readonly List<Valuation> values = [];
    private readonly List<Flow> flows = [];

    public ValuationCadence Cadence { get; init; } = ValuationCadence.Monthly;
    public LocalDate? ClosedOn { get; init; }
    public decimal? UcShareFloorPct { get; init; }

    public ItemId Id => id;

    public EnvelopeFacts Value(LocalDate on, decimal amount)
    {
        values.Add(new Valuation(Guid.NewGuid(), id, on, M(amount, money), null));
        return this;
    }

    public EnvelopeFacts Flow(LocalDate on, FlowKind kind, decimal amount, string? category = null)
    {
        flows.Add(new Flow(Guid.NewGuid(), id, on, kind, M(amount, money), category, null, null));
        return this;
    }

    public EnvelopePerformance.Facts Build()
    {
        var shares = owners.Length == 0 ? [(DefaultOwner, 10_000)] : owners;
        var item = new ItemAccess(id, name, Visibility.Shared, shares.Select(o => new Owner(o.Person, PersonKind.Adult, o.Bp)).ToList())
        {
            Currency = money.Code,
            ArchivedAt = ClosedOn is { } closed ? closed.AtMidnight().InUtc().ToInstant() : null,
        };
        var envelope = new Envelope(id, kind, "Banque", null, null, Cadence, null, null, ClosedOn, UcShareFloorPct);
        return new EnvelopePerformance.Facts(envelope, item, values, flows, money);
    }
}
