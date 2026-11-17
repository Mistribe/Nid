using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Investments.Application;

/// <summary>Issue #81: the room of <see cref="EnvelopeInsights.Room"/>, summed over the open livrets the reader reads.</summary>
public sealed class SavingsRoom(
    IVisibleItems visible, IEnvelopeStore envelopes, EnvelopePerformance performance, IItemRegistry items, IHouseholdSettings household) : ISavingsRoom
{
    public async Task<decimal> RoomAsync(PersonId reader, LocalDate today, CancellationToken ct)
    {
        // Active items only: the open livrets.
        var ids = await visible.ForAsync(reader, VisibilityScope.Me, ct);
        var livrets = (await envelopes.FindManyAsync(ids.ToList(), ct)).Where(e => e.Regulated is not null).ToList();
        if (livrets.Count == 0)
            return 0m;
        var facts = await performance.FactsManyAsync(livrets, await items.FindManyAsync(livrets.Select(e => e.ItemId).ToList(), ct), ct);
        var reference = await household.ReferenceCurrencyAsync(ct);
        // ponytail: a livret in another currency than the reference is left out (regulated livrets are in euros); convert if that changes.
        return (facts?.Values ?? []).Where(f => f.Currency == reference).Sum(f => EnvelopeInsights.Room(f, today)?.Room ?? 0m);
    }
}
