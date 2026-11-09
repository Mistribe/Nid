using System.Globalization;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Synapse.Abstractions;
using CoreSeriesPoint = Nid.Core.Contracts.SeriesPoint;

namespace Nid.Investments.Application;

/// <summary>
/// An envelope's value and net contributions (<c>envelope:&lt;id&gt;:value</c>, <c>:contributions</c>, what its value chart draws) and a
/// holding's unit price (<c>holding:&lt;envelope&gt;:&lt;holding&gt;:price</c>, from the envelope's snapshots). They follow item access, not the selector.
/// </summary>
public sealed class InvestmentSeries(
    IVisibleItems visible, IItemRegistry items, IAccessPolicy policy, IAccessDenials denials, IHoldingStore holdings, IInvoker invoker) : ISeriesContribution
{
    private const string Value = "value";
    private const string Contributions = "contributions";

    public string Module => "investments";

    public async Task<IReadOnlyList<SeriesInfo>> ListAsync(PersonId reader, VisibilityScope scope, string? q, IReadOnlyCollection<string> kinds, CancellationToken ct)
    {
        var ids = await visible.ForAsync(reader, scope, ct);
        var list = new List<SeriesInfo>();
        foreach (var (kind, suffix) in new[] { (SeriesKinds.EnvelopeValue, Value), (SeriesKinds.EnvelopeContributions, Contributions) })
            list.AddRange((await items.SearchAsync(ids, Module, "envelope", SeriesSearch.NameFilter(q, kinds, kind, "envelope", suffix), ct))
                .Select(e => EnvelopeInfo(e, suffix)));

        var priced = await holdings.PricedAsync(ids, SeriesSearch.NameFilter(q, kinds, SeriesKinds.HoldingPrice, "holding", "price"), ct);
        var envelopes = await items.FindManyAsync(priced.Select(h => h.Envelope).Distinct().ToList(), ct);
        list.AddRange(priced.Where(h => envelopes.ContainsKey(h.Envelope)).Select(h => HoldingInfo(h, envelopes[h.Envelope])));
        return list;
    }

    public async Task<SeriesPoints?> PointsAsync(PersonId reader, VisibilityScope scope, string key, LocalDate? from, LocalDate? to, CancellationToken ct)
    {
        var parts = key.Split(':');
        return parts switch
        {
            ["envelope", var id, Value or Contributions] when Guid.TryParse(id, out var guid) => await EnvelopeAsync(new ItemId(guid), parts[2], from, to, ct),
            ["holding", var env, var holding, "price"] when Guid.TryParse(env, out var e) && Guid.TryParse(holding, out var h) => await PriceAsync(reader, new ItemId(e), h, from, to, ct),
            _ => null,
        };
    }

    private async Task<SeriesPoints?> EnvelopeAsync(ItemId id, string suffix, LocalDate? from, LocalDate? to, CancellationToken ct)
    {
        var series = await invoker.InvokeAsync(new GetEnvelopeSeriesQuery(id), ct); // item access, denials journaled
        if (!series.TryGetValue(out var view) || await items.FindAsync(id, ct) is not { } item) return null;
        return new SeriesPoints(EnvelopeInfo(item, suffix),
            SeriesWindow.Within(view.Points.Select(p => new CoreSeriesPoint(p.AsOf, Parse(suffix == Value ? p.Value.Amount : p.NetContributions.Amount))), from, to));
    }

    private async Task<SeriesPoints?> PriceAsync(PersonId reader, ItemId envelope, Guid holdingId, LocalDate? from, LocalDate? to, CancellationToken ct)
    {
        if ((await policy.CheckAsync(reader, envelope, AccessKind.Read, ct)).TryGetFailure(out _))
        {
            await denials.RecordAsync(reader, envelope, "read", ct); // as the item-scoped pipeline does
            return null;
        }

        var holding = (await holdings.HoldingsAsync(envelope, ct)).Select(h => h.Holding).FirstOrDefault(h => h.Id == holdingId);
        if (holding is null || !HoldingWire.HasUnits(holding.Kind) || await items.FindAsync(envelope, ct) is not { } item) return null;

        var points = (await holdings.SnapshotsAsync(envelope, ct))
            .SelectMany(s => s.Lines.Where(l => l.HoldingId == holdingId && l.UnitPrice > 0).Select(l => new CoreSeriesPoint(s.AsOf, l.UnitPrice!.Value)));
        return new SeriesPoints(HoldingInfo(holding, item), SeriesWindow.Within(points, from, to));
    }

    private static SeriesInfo EnvelopeInfo(ItemAccess item, string suffix)
        => new($"envelope:{item.Id.Value}:{suffix}", suffix == Value ? SeriesKinds.EnvelopeValue : SeriesKinds.EnvelopeContributions,
            item.DisplayName, $"currency:{item.Currency}", SeriesFrequencies.Irregular, null, null, item.Id);

    private static SeriesInfo HoldingInfo(EnvelopeHolding holding, ItemAccess envelope)
        => new($"holding:{envelope.Id.Value}:{holding.Id}:price", SeriesKinds.HoldingPrice, holding.Name, $"currency:{envelope.Currency}",
            SeriesFrequencies.Irregular, null, null, envelope.Id);

    private static decimal Parse(string amount) => decimal.Parse(amount, CultureInfo.InvariantCulture);
}
