using Nid.Core.Contracts;
using Nid.Investments.Domain;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Investments.Application;

public sealed record GetEnvelopeSeriesQuery(ItemId ItemId) : IQuery<EnvelopeSeries>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

[RequestHandler<GetEnvelopeSeriesQuery, EnvelopeSeries>]
public sealed class GetEnvelopeSeriesHandler(EnvelopePerformance performance)
    : IRequestHandler<GetEnvelopeSeriesQuery, EnvelopeSeries>
{
    public async ValueTask<Result<EnvelopeSeries>> HandleAsync(GetEnvelopeSeriesQuery query, CancellationToken ct = default)
    {
        var facts = await performance.FactsAsync(query.ItemId, ct);
        if (facts.TryGetFailure(out var failure))
            return Result.Failure<EnvelopeSeries>(failure!);
        facts.TryGetValue(out var f);
        var points = EnvelopeMath.Series(f!.Values, f.Flows, f.Currency)
            .Select(p => new SeriesPoint(p.AsOf, AmountView.From(p.Value), AmountView.From(p.NetContributions)))
            .ToList();
        return Result.Success(new EnvelopeSeries(query.ItemId, points));
    }
}
