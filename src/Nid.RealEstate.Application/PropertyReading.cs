using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Application;

/// <summary>What a property's detail shows: its facts, the items of its readable linked loans (by name), and its figures.</summary>
public sealed record PropertyRead(IReadOnlyList<Valuation> Values, IReadOnlyList<Flow> Flows, IReadOnlyList<ItemAccess> Loans, PropertyFigures Figures);

/// <summary>Spec 3C1 §5: a property's facts and figures today, read as the ambient actor, <c>reader</c>.</summary>
public sealed class PropertyReading(IValuations valuations, IFlows flows)
{
    /// <summary>Every current fact, whenever dated.</summary>
    private static readonly DateRange Everything = DateRange.Create(LocalDate.MinIsoValue, null).TryGetValue(out var r) ? r : throw new InvalidOperationException();

    public async Task<Result<PropertyRead>> ReadAsync(StoredProperty property, ItemAccess item, PersonId reader, LocalDate today, CancellationToken ct)
    {
        var series = await valuations.SeriesAsync(property.ItemId, Everything, ct);
        if (series.TryGetFailure(out var failure))
            return Result.Failure<PropertyRead>(failure!);
        var listed = await flows.ListAsync(property.ItemId, Everything, null, ct);
        if (listed.TryGetFailure(out failure))
            return Result.Failure<PropertyRead>(failure!);
        series.TryGetValue(out var values);
        listed.TryGetValue(out var flowList);

        return Result.Success(new PropertyRead(values!, flowList!, [],
            PropertyMath.At(property.Details, item.Visibility == Visibility.Shared, values!, flowList!, [], today)));
    }
}
