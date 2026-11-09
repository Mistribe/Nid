using System.Globalization;
using Microsoft.Extensions.Logging;
using Nid.Core.Contracts;
using Nid.Core.Domain.Series;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Series;

/// <summary>The series explorer's catalogue: every module's series the reader can see, filtered by name, 50 to a page.</summary>
public sealed record ListSeriesQuery(VisibilityScope Scope, string? Search, int Page) : IQuery<SeriesCatalogView>
{
    public const int PageSize = 50;
}

/// <summary><c>Keys</c> is the raw <c>s</c> parameter (1 to 6 comma-separated keys); the points come back in that order.</summary>
public sealed record GetSeriesPointsQuery(VisibilityScope Scope, string? Keys, LocalDate? From, LocalDate? To) : IQuery<SeriesPointsView>;

public sealed record SeriesCatalogView(IReadOnlyList<SeriesInfo> Items, int Total, int Page) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.Select(i => i.Item).OfType<ItemId>();
}

public sealed record SeriesPointView(LocalDate Date, string Value, string? Base100);

public sealed record SeriesPointsItemView(
    string Key, string Kind, string? Name, string Unit, string Frequency, ItemId? Item, IReadOnlyList<SeriesPointView> Points);

public sealed record SeriesPointsView(IReadOnlyList<SeriesPointsItemView> Series, LocalDate? BaseDate) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Series.Select(s => s.Item).OfType<ItemId>();
}

public static class SeriesText
{
    public static string Of(decimal value) => value.ToString("0.##########", CultureInfo.InvariantCulture);
}

[RequestHandler<ListSeriesQuery, SeriesCatalogView>]
public sealed class ListSeriesHandler(IEnumerable<ISeriesContribution> contributions, IActorAccessor actor, ILogger<ListSeriesHandler> logger)
    : IRequestHandler<ListSeriesQuery, SeriesCatalogView>
{
    private static readonly StringComparer Names = StringComparer.Create(CultureInfo.GetCultureInfo("fr-FR"), ignoreCase: true);

    public async ValueTask<Result<SeriesCatalogView>> HandleAsync(ListSeriesQuery query, CancellationToken ct = default)
    {
        var reader = actor.Current!.PersonId!.Value;
        var all = new List<(string Module, SeriesInfo Info)>();
        foreach (var contribution in contributions)
        {
            try
            {
                all.AddRange((await contribution.ListAsync(reader, query.Scope, query.Search, ct)).Select(i => (contribution.Module, i)));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "The series of module {Module} could not be listed", contribution.Module);
            }
        }

        // Reference series first (fx last among them), then the items' by module and name.
        var ordered = all.Where(s => s.Info.Item is null)
            .OrderBy(s => s.Info.Kind == SeriesKinds.Fx).ThenBy(s => s.Info.Key, StringComparer.Ordinal)
            .Concat(all.Where(s => s.Info.Item is not null)
                .OrderBy(s => s.Module, StringComparer.Ordinal).ThenBy(s => s.Info.Name, Names).ThenBy(s => s.Info.Key, StringComparer.Ordinal))
            .Select(s => s.Info).ToList();

        var page = Math.Max(query.Page, 1);
        return Result.Success(new SeriesCatalogView(ordered.Skip((page - 1) * ListSeriesQuery.PageSize).Take(ListSeriesQuery.PageSize).ToList(), ordered.Count, page));
    }
}

[RequestHandler<GetSeriesPointsQuery, SeriesPointsView>]
public sealed class GetSeriesPointsHandler(IEnumerable<ISeriesContribution> contributions, IActorAccessor actor)
    : IRequestHandler<GetSeriesPointsQuery, SeriesPointsView>
{
    public async ValueTask<Result<SeriesPointsView>> HandleAsync(GetSeriesPointsQuery query, CancellationToken ct = default)
    {
        if (!SeriesKeys.TryParse(query.Keys, out var keys))
            return Result.Failure<SeriesPointsView>(new Failure(SeriesCodes.InvalidKey,
                "s holds 1 to 6 distinct series keys such as cpi or fx:EUR.", new Dictionary<string, object?> { ["field"] = "s" }));

        var reader = actor.Current!.PersonId!.Value;
        var found = new List<SeriesPoints>();
        foreach (var key in keys)
        {
            SeriesPoints? answer = null;
            foreach (var contribution in contributions)
                if ((answer = await contribution.PointsAsync(reader, query.Scope, key, query.From, query.To, ct)) is not null)
                    break;
            if (answer is null)
                return Result.Failure<SeriesPointsView>(new Failure(SeriesCodes.NotFound, $"No series '{key}'.", null));
            found.Add(answer);
        }

        var rebased = SeriesRebase.Rebase(found.Select(f => new RebaseInput(f.Info.Unit != SeriesUnits.Percent, f.Points)).ToList());
        var series = found.Select((f, i) => new SeriesPointsItemView(f.Info.Key, f.Info.Kind, f.Info.Name, f.Info.Unit, f.Info.Frequency, f.Info.Item,
            f.Points.Select((p, j) => new SeriesPointView(p.Date, SeriesText.Of(p.Value), rebased.Base100[i][j] is { } b ? SeriesText.Of(b) : null)).ToList())).ToList();
        return Result.Success(new SeriesPointsView(series, rebased.BaseDate));
    }
}
