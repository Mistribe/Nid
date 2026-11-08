using Nid.Core.Application.Scenarios;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Projection;

/// <summary>
/// Spec §14: the base and 1 to 3 saved scenarios (projections, purchases or sales) on the request's grid and scope; each scenario's stored
/// horizon and scope are ignored.
/// </summary>
public sealed record CompareScenariosQuery(VisibilityScope Scope, ProjectionHorizon Horizon, IReadOnlyList<ItemId> Ids) : IQuery<ProjectionCompareView>;

/// <summary>
/// Spec §14.1: one compared scenario, its projection on the base's grid and its gap at the horizon (scenario − base). <c>Result</c>'s
/// warnings are only those the base does not carry.
/// </summary>
public sealed record ComparedScenarioView(ItemId Id, string Name, string Kind, ProjectionView Result, string GapNominal, string GapReal);

/// <summary>Spec §14.1: the base, computed once, and the scenarios in the order of the request's ids.</summary>
public sealed record ProjectionCompareView(ProjectionView Base, IReadOnlyList<ComparedScenarioView> Scenarios) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems()
        => Base.ExposedItems().Concat(Scenarios.SelectMany(s => s.Result.ExposedItems().Prepend(s.Id))).Distinct();
}

[RequestHandler<CompareScenariosQuery, ProjectionCompareView>]
public sealed class CompareScenariosHandler(
    IVisibleItems visible, IItemRegistry items, IScenarioStore store, ProjectionScenarioReader reader, SaleScenarioReader sales,
    IHouseholdCalendar calendar, IActorAccessor actor)
    : IRequestHandler<CompareScenariosQuery, ProjectionCompareView>
{
    public const int MaxScenarios = 3;

    public async ValueTask<Result<ProjectionCompareView>> HandleAsync(CompareScenariosQuery query, CancellationToken ct = default)
    {
        if (query.Ids.Count is 0 or > MaxScenarios || query.Ids.Distinct().Count() != query.Ids.Count)
            return Result.Failure<ProjectionCompareView>(FieldErrors.Many(ProjectionCodes.InvalidField, "Send 1 to 3 distinct scenario ids.",
                [new FieldError("ids", ProjectionCodes.InvalidField, "Send 1 to 3 distinct scenario ids.")]));

        // Spec §14.1: a scenario is compared only if the list would show it in this scope; every id is checked before anything is computed.
        var seen = await visible.ForAsync(actor.Current!.PersonId!.Value, query.Scope, ct);
        var found = new List<(ItemAccess Item, StoredScenario Stored)>();
        foreach (var id in query.Ids)
        {
            var loaded = seen.Contains(id)
                ? await ScenarioResults.LoadAsync(items, store, id, ct, kind: null)
                : Result.Failure<(ItemAccess, StoredScenario)>(new NotFoundFailure("scenario", id.ToString()));
            if (loaded.TryGetFailure(out var failure)) return Result.Failure<ProjectionCompareView>(failure!);
            loaded.TryGetValue(out var scenario);
            found.Add(scenario);
        }

        var horizon = new ScenarioHorizon(query.Horizon.Years, query.Horizon.RetireOf);
        var today = await calendar.TodayAsync(ct);
        ProjectionView? based = null;
        var compared = new List<ComparedScenarioView>();
        // ponytail: the base's facts are read again for each scenario (at most 3); share one read if the limit grows.
        foreach (var (item, stored) in found)
        {
            var document = stored.Kind == ScenarioKinds.Projection ? stored.Projection! with { Horizon = horizon }
                : stored.Kind == ScenarioKinds.PropertySale ? await sales.ProjectionAsync(stored.Sale!, query.Scope, horizon, ct)
                : new ProjectionScenario(1, horizon, new ProjectionOverrides(null, null, null, null, null),
                [
                    new ProjectionEvent(Guid.NewGuid(), ProjectionEventKinds.PropertyPurchase, new EventDate(today, null, null), null, null, null,
                        null, null, null, null, null, null, null, null, item.Id),
                ]);
            var read = await reader.ReadAsync(document, query.Scope, ct);
            if (read.TryGetFailure(out var failure)) return Result.Failure<ProjectionCompareView>(failure!);
            read.TryGetValue(out var reading);
            var comparison = ProjectionComparisonView.From(reading!);
            based ??= comparison.Base;
            compared.Add(new ComparedScenarioView(
                item.Id, item.DisplayName, stored.Kind,
                comparison.Scenario with { Warnings = comparison.Scenario.Warnings.Except(comparison.Base.Warnings).ToList() },
                comparison.GapNominal, comparison.GapReal));
        }
        return Result.Success(new ProjectionCompareView(based!, compared));
    }
}
