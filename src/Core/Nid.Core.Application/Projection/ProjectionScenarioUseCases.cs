using Nid.Core.Application.Scenarios;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Projection;

/// <summary>Spec §7: a projection scenario's document against the base, in the reader's scope; nothing is written.</summary>
public sealed record SimulateProjectionScenarioQuery(VisibilityScope Scope, ProjectionScenario Document) : IQuery<ProjectionComparisonView>;

/// <summary>Spec §6 and §7: a projection scenario saved as a registry item, beside the purchases; the document is checked as §4.5 says.</summary>
public sealed record CreateProjectionScenarioCommand(
    string Name, Visibility Visibility, IReadOnlyList<OwnerShare> Owners, VisibilityScope Scope, ProjectionScenario Document)
    : ICommand<ItemId>, ICreatesItem;

/// <summary>Spec §6: name, visibility, scope and document replaced whole; the owners are kept, and so are the events the writer cannot see.</summary>
public sealed record ReplaceProjectionScenarioCommand(ItemId ItemId, string Name, Visibility Visibility, VisibilityScope Scope, ProjectionScenario Document)
    : ICommand<ProjectionScenarioView>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>
/// Spec §6 and §7: the document as the reader may see it, against the base, computed now on the scenario's scope; with the scenario's
/// bands when <c>Ranges</c> (ranges spec §4).
/// </summary>
public sealed record GetProjectionScenarioQuery(ItemId ItemId, bool Ranges = false) : IQuery<ProjectionScenarioView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

[RequestHandler<SimulateProjectionScenarioQuery, ProjectionComparisonView>]
public sealed class SimulateProjectionScenarioHandler(ProjectionScenarioReader reader, IHouseholdSettings household, IHouseholdCalendar calendar)
    : IRequestHandler<SimulateProjectionScenarioQuery, ProjectionComparisonView>
{
    public async ValueTask<Result<ProjectionComparisonView>> HandleAsync(SimulateProjectionScenarioQuery query, CancellationToken ct = default)
    {
        if (ProjectionScenarioRefusals.Of(query.Document, await household.ProjectionAssumptionsAsync(ct), await calendar.TodayAsync(ct)) is { } refused)
            return Result.Failure<ProjectionComparisonView>(refused);
        var read = await reader.ReadAsync(query.Document, query.Scope, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<ProjectionComparisonView>(failure!);
        read.TryGetValue(out var reading);
        return Result.Success(ProjectionComparisonView.From(reading!));
    }
}

/// <summary>The projection scenarios' common reads.</summary>
internal static class ProjectionScenarios
{
    public static async Task<Result<ProjectionScenarioView>> ViewAsync(
        ProjectionScenarioReader reader, IAccessPolicy policy, PersonId actor, ItemAccess item, StoredScenario stored, CancellationToken ct,
        bool ranges = false)
    {
        var read = await reader.ReadAsync(stored.Projection!, stored.Scope, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<ProjectionScenarioView>(failure!);
        read.TryGetValue(out var reading);
        var canWrite = (await policy.CheckAsync(actor, item.Id, AccessKind.Write, ct)).IsSuccess;
        return Result.Success(new ProjectionScenarioView(
            item.Id, item.DisplayName, ScenarioResults.Wire(item.Visibility), stored.Scope == VisibilityScope.Me ? "me" : "household", canWrite,
            ProjectionComparisonView.From(reading!, ranges)));
    }

    /// <summary>Inflation spec C15: a saved scenario keeps no yearly override of a past year.</summary>
    public static ProjectionScenario WithoutPastYears(ProjectionScenario doc, NodaTime.LocalDate today)
        => doc with { Assumptions = ProjectionRules.WithoutPastYears(doc.Assumptions, today) };

    /// <summary>
    /// Spec §6: the writer's document with the stored events they were not shown put back, each at its index when it still fits
    /// (the writer's events keep their order around them).
    /// </summary>
    public static ProjectionScenario Merge(ProjectionScenario written, IReadOnlyList<(int Index, ProjectionEvent Event)> hidden)
    {
        var events = written.Events.ToList();
        foreach (var (index, e) in hidden.OrderBy(h => h.Index))
            events.Insert(Math.Min(index, events.Count), e);
        return written with { Events = events };
    }
}

[RequestHandler<GetProjectionScenarioQuery, ProjectionScenarioView>]
public sealed class GetProjectionScenarioHandler(
    IItemRegistry items, IScenarioStore store, ProjectionScenarioReader reader, IAccessPolicy policy, IActorAccessor actor)
    : IRequestHandler<GetProjectionScenarioQuery, ProjectionScenarioView>
{
    public async ValueTask<Result<ProjectionScenarioView>> HandleAsync(GetProjectionScenarioQuery query, CancellationToken ct = default)
    {
        var loaded = await ScenarioResults.LoadAsync(items, store, query.ItemId, ct, ScenarioKinds.Projection);
        if (loaded.TryGetFailure(out var failure)) return Result.Failure<ProjectionScenarioView>(failure!);
        loaded.TryGetValue(out var found);
        return await ProjectionScenarios.ViewAsync(reader, policy, actor.Current!.PersonId!.Value, found.Item, found.Stored, ct, query.Ranges);
    }
}

[RequestHandler<CreateProjectionScenarioCommand, ItemId>]
public sealed class CreateProjectionScenarioHandler(
    IItemRegistry items, IScenarioStore store, ProjectionScenarioReader reader, IUnitOfWork unitOfWork, IHouseholdCalendar calendar)
    : IRequestHandler<CreateProjectionScenarioCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(CreateProjectionScenarioCommand command, CancellationToken ct = default)
    {
        command = command with { Document = ProjectionScenarios.WithoutPastYears(command.Document, await calendar.TodayAsync(ct)) };
        if ((ScenarioResults.Name(command.Name) ?? await reader.RefusalAsync(command.Document, command.Scope, ct)) is { } refused)
            return Result.Failure<ItemId>(refused);

        // The Core item and the document row: one transaction, or an orphan item when the second write fails.
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var created = await items.CreateAsync(new NewItem("core", "scenario", command.Name, command.Visibility, null, command.Owners), ct);
        if (created.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(FieldErrors.At(failure!, "owners"));
        created.TryGetValue(out var id);
        await store.SaveAsync(new StoredScenario(id, ScenarioKinds.Projection, command.Scope, null, command.Document), ct);
        await transaction.CommitAsync(ct);
        return Result.Success(id);
    }
}

[RequestHandler<ReplaceProjectionScenarioCommand, ProjectionScenarioView>]
public sealed class ReplaceProjectionScenarioHandler(
    IItemRegistry items, IItemRules rules, IScenarioStore store, ProjectionScenarioReader reader, IAccessPolicy policy, IActorAccessor actor,
    IHouseholdSettings household, IHouseholdCalendar calendar, IUnitOfWork unitOfWork)
    : IRequestHandler<ReplaceProjectionScenarioCommand, ProjectionScenarioView>
{
    public async ValueTask<Result<ProjectionScenarioView>> HandleAsync(ReplaceProjectionScenarioCommand command, CancellationToken ct = default)
    {
        var loaded = await ScenarioResults.LoadAsync(items, store, command.ItemId, ct, ScenarioKinds.Projection);
        if (loaded.TryGetFailure(out var failure)) return Result.Failure<ProjectionScenarioView>(failure!);
        loaded.TryGetValue(out var found);
        // #294: an adult who may edit the scenario does not own it: its visibility stays with its owners.
        if (command.Visibility != found.Item.Visibility && !rules.CanCreate(actor.Current!.PersonId!.Value, found.Item.Owners))
            return Result.Failure<ProjectionScenarioView>(new UnauthorizedFailure("Only the scenario's owners change its visibility.", null));
        if ((ScenarioResults.Name(command.Name) ?? await reader.RefusalAsync(command.Document, command.Scope, ct)) is { } refused)
            return Result.Failure<ProjectionScenarioView>(refused);

        // Spec §6: the writer was sent the document without the events on items they cannot see (in the scope it was shown in);
        // those are kept, not re-checked against the writer. The whole must still hold at most 30 events, each id once.
        var hidden = await reader.HiddenAsync(found.Stored.Projection!, found.Stored.Scope, ct);
        var today = await calendar.TodayAsync(ct);
        var document = ProjectionScenarios.WithoutPastYears(ProjectionScenarios.Merge(command.Document, hidden), today);
        if (ProjectionScenarioRefusals.Of(document, await household.ProjectionAssumptionsAsync(ct), today) is { } merged)
            return Result.Failure<ProjectionScenarioView>(merged);

        var owners = found.Item.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList();
        var stored = found.Stored with { Scope = command.Scope, Projection = document };
        await using (var transaction = await unitOfWork.BeginAsync(ct))
        {
            var replaced = await items.ReplaceAsync(command.ItemId, command.Name, command.Visibility, owners, ct);
            if (replaced.TryGetFailure(out failure)) return Result.Failure<ProjectionScenarioView>(failure!);
            await store.SaveAsync(stored, ct);
            await transaction.CommitAsync(ct);
        }
        var item = found.Item with { DisplayName = command.Name, Visibility = command.Visibility };
        return await ProjectionScenarios.ViewAsync(reader, policy, actor.Current!.PersonId!.Value, item, stored, ct);
    }
}
