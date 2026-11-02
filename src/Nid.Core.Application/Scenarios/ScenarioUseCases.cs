using System.Globalization;
using Nid.Core.Application.Dashboard;
using Nid.Core.Application.Projection;
using Nid.Core.Contracts;
using Nid.Core.Domain.Scenarios;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Scenarios;

/// <summary>Spec §6: the reader's active scenarios in the scope.</summary>
public sealed record ListScenariosQuery(VisibilityScope Scope) : IQuery<ScenarioList>;

/// <summary>Spec §6: the document as the reader may see it, and its result computed now on the scenario's scope.</summary>
public sealed record GetScenarioQuery(ItemId ItemId) : IQuery<ScenarioView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

public sealed record CreateScenarioCommand(
    string Name, Visibility Visibility, IReadOnlyList<OwnerShare> Owners, VisibilityScope Scope, PropertyPurchase Document)
    : ICommand<ItemId>, ICreatesItem;

/// <summary>Spec §6: name, visibility, scope and document replaced whole; the owners are kept.</summary>
public sealed record ReplaceScenarioCommand(ItemId ItemId, string Name, Visibility Visibility, VisibilityScope Scope, PropertyPurchase Document)
    : ICommand<ScenarioView>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>Spec §6: archives the item, the way Nid removes one; the list reads active items only.</summary>
public sealed record DeleteScenarioCommand(ItemId ItemId) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>Spec §6: the same calculation on a document that is not stored; nothing is written.</summary>
public sealed record SimulateScenarioQuery(VisibilityScope Scope, PropertyPurchase Document) : IQuery<ScenarioResultView>;

public sealed record GetScenarioDefaultsQuery(VisibilityScope Scope) : IQuery<ScenarioDefaultsView>;

/// <summary>The calculator's results mapped to the wire, and the scenarios' common reads.</summary>
internal static class ScenarioResults
{
    /// <summary>Spec §4.3's order.</summary>
    private static readonly string[] Order =
    [
        "funding_gap", "funding_source_hidden", "debt_ratio_above_hcsf", "savings_exhausted", "no_income", "fx_missing",
        "tax_estimate_unavailable", "purchase_in_past",
    ];

    private static readonly StringComparer French = StringComparer.Create(CultureInfo.GetCultureInfo("fr-FR"), ignoreCase: true);

    public static ScenarioResultView Of(ScenarioReading r)
    {
        var capacity = ScenarioCalculator.Capacity(r.Document, r.Facts);
        var projection = ScenarioCalculator.Project(r.Document, r.Facts, capacity);
        var warnings = new List<ScenarioWarningView>();
        // The gap of the document the reader sees, hidden lines left out: when the author balanced the plan, it equals their total.
        if (ScenarioRules.FundingGap(r.Document) is { } gap) warnings.Add(new("funding_gap", PropertyPurchaseView.Amount(gap)));
        if (r.HiddenFunding > 0) warnings.Add(new("funding_source_hidden", r.HiddenFunding.ToString(CultureInfo.InvariantCulture)));
        warnings.AddRange(capacity.Warnings.Select(w => new ScenarioWarningView(w, null)));
        warnings.AddRange(projection.Warnings.Select(w => new ScenarioWarningView(w,
            w == "savings_exhausted" && projection.SavingsExhaustedIn is { } m ? $"{m.Year:0000}-{m.Month:00}" : null)));
        warnings.AddRange(r.Missing.Select(c => new ScenarioWarningView("fx_missing", c)));
        if (r.TaxUnavailable) warnings.Add(new("tax_estimate_unavailable", null));
        return ScenarioResultView.From(capacity, projection, r.Facts.Loans, warnings.OrderBy(w => Array.IndexOf(Order, w.Code)).ToList(), r.Currency);
    }

    /// <summary>
    /// An active scenario of <paramref name="kind"/> (<see cref="ScenarioKinds"/>; null: of any kind, deleting). The routes of one kind
    /// answer 404 for a scenario of the other (projection spec §7).
    /// </summary>
    public static async Task<Result<(ItemAccess Item, StoredScenario Stored)>> LoadAsync(
        IItemRegistry items, IScenarioStore store, ItemId id, CancellationToken ct, string? kind = ScenarioKinds.PropertyPurchase)
    {
        var item = await items.FindAsync(id, ct);
        var stored = await store.FindAsync(id, ct);
        // An archived scenario is deleted; an item of another kind is no scenario.
        return item is { ArchivedAt: null } && stored is not null && (kind is null || stored.Kind == kind)
            ? Result.Success((item, stored))
            : Result.Failure<(ItemAccess, StoredScenario)>(new NotFoundFailure("scenario", id.ToString()));
    }

    public static async Task<Result<ScenarioView>> ViewAsync(
        ScenarioFactsReader facts, IAccessPolicy policy, PersonId reader, ItemAccess item, StoredScenario stored, CancellationToken ct)
    {
        var read = await facts.ReadAsync(stored.Document!, stored.Scope, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<ScenarioView>(failure!);
        read.TryGetValue(out var reading);
        var canWrite = (await policy.CheckAsync(reader, item.Id, AccessKind.Write, ct)).IsSuccess;
        return Result.Success(new ScenarioView(
            item.Id, item.DisplayName, Wire(item.Visibility), stored.Scope == VisibilityScope.Me ? "me" : "household",
            PropertyPurchaseView.From(reading!.Document), Of(reading), canWrite));
    }

    public static Failure? Name(string name)
        => string.IsNullOrWhiteSpace(name)
            ? FieldErrors.At(new Failure(ScenarioCodes.InvalidField, "A scenario has a name.", null), "name")
            : null;

    public static string Wire(Visibility visibility) => visibility == Visibility.Private ? "private" : "shared";

    public static IEnumerable<T> ByName<T>(IEnumerable<T> rows, Func<T, string> name) => rows.OrderBy(name, French);
}

[RequestHandler<ListScenariosQuery, ScenarioList>]
public sealed class ListScenariosHandler(
    IVisibleItems visible, IItemRegistry items, IScenarioStore store, ScenarioFactsReader facts, ProjectionScenarioReader projections,
    IActorAccessor actor)
    : IRequestHandler<ListScenariosQuery, ScenarioList>
{
    public async ValueTask<Result<ScenarioList>> HandleAsync(ListScenariosQuery query, CancellationToken ct = default)
    {
        var ids = await visible.ForAsync(actor.Current!.PersonId!.Value, query.Scope, ct);
        var stored = await store.FindManyAsync(ids, ct);
        var accesses = await items.FindManyAsync(stored.Select(s => s.Item).ToList(), ct);
        var rows = new List<ScenarioListItemView>();
        // shortcut: each scenario is read and computed on its own scope, one after the other; batch the reads if lists grow long.
        foreach (var scenario in stored)
        {
            var item = accesses[scenario.Item];
            var row = scenario.Kind == ScenarioKinds.Projection
                ? await ProjectionRowAsync(item, scenario, ct)
                : await PurchaseRowAsync(item, scenario, ct);
            if (row.TryGetFailure(out var failure)) return Result.Failure<ScenarioList>(failure!);
            row.TryGetValue(out var view);
            rows.Add(view!);
        }
        return Result.Success(new ScenarioList(ScenarioResults.ByName(rows, r => r.Name).ToList()));
    }

    private async Task<Result<ScenarioListItemView>> PurchaseRowAsync(ItemAccess item, StoredScenario scenario, CancellationToken ct)
    {
        var read = await facts.ReadAsync(scenario.Document!, scenario.Scope, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<ScenarioListItemView>(failure!);
        read.TryGetValue(out var reading);
        var result = ScenarioResults.Of(reading!);
        return Result.Success(new ScenarioListItemView(
            item.Id, ScenarioKinds.PropertyPurchase, item.DisplayName, scenario.Document!.Use == PurchaseUse.Rental ? "rental" : "primary",
            CapacityView.Money(scenario.Document.Price, reading!.Currency), result.Capacity.NewPayment, result.Capacity.DebtRatioAfter,
            result.Gap, item.OwnerViews(), ScenarioResults.Wire(item.Visibility)));
    }

    /// <summary>
    /// Projection spec §7: the nominal gap at the horizon, as the reader sees the scenario; null when its horizon can no longer be read
    /// (a retirement whose person lost their birth date, or now past) or its projection fails for any reason, so one broken scenario
    /// does not hide the others.
    /// </summary>
    private async Task<Result<ScenarioListItemView>> ProjectionRowAsync(ItemAccess item, StoredScenario scenario, CancellationToken ct)
    {
        var read = await projections.ReadAsync(scenario.Projection!, scenario.Scope, ct);
        DashboardMoneyView? gap = null;
        if (read.TryGetValue(out var reading))
        {
            try
            {
                gap = new DashboardMoneyView(ProjectionComparisonView.From(reading!).GapNominal, reading!.Base.Reference.Code);
            }
            catch (InvalidOperationException)
            {
                // grids that diverged: no gap for this row.
            }
        }
        return Result.Success(new ScenarioListItemView(
            item.Id, ScenarioKinds.Projection, item.DisplayName, null, null, null, null, gap, item.OwnerViews(), ScenarioResults.Wire(item.Visibility)));
    }
}

[RequestHandler<GetScenarioQuery, ScenarioView>]
public sealed class GetScenarioHandler(
    IItemRegistry items, IScenarioStore store, ScenarioFactsReader facts, IAccessPolicy policy, IActorAccessor actor)
    : IRequestHandler<GetScenarioQuery, ScenarioView>
{
    public async ValueTask<Result<ScenarioView>> HandleAsync(GetScenarioQuery query, CancellationToken ct = default)
    {
        var loaded = await ScenarioResults.LoadAsync(items, store, query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure)) return Result.Failure<ScenarioView>(failure!);
        loaded.TryGetValue(out var found);
        return await ScenarioResults.ViewAsync(facts, policy, actor.Current!.PersonId!.Value, found.Item, found.Stored, ct);
    }
}

[RequestHandler<CreateScenarioCommand, ItemId>]
public sealed class CreateScenarioHandler(IItemRegistry items, IScenarioStore store, ScenarioFactsReader facts, IUnitOfWork unitOfWork)
    : IRequestHandler<CreateScenarioCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(CreateScenarioCommand command, CancellationToken ct = default)
    {
        if ((ScenarioResults.Name(command.Name) ?? (await facts.RefusalAsync(command.Document, command.Scope, ct)).Refused) is { } refused)
            return Result.Failure<ItemId>(refused);

        // The Core item and the document row: one transaction, or an orphan item when the second write fails.
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var created = await items.CreateAsync(new NewItem("core", "scenario", command.Name, command.Visibility, null, command.Owners), ct);
        if (created.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(FieldErrors.At(failure!, "owners"));
        created.TryGetValue(out var id);
        await store.SaveAsync(new StoredScenario(id, ScenarioKinds.PropertyPurchase, command.Scope, command.Document), ct);
        await transaction.CommitAsync(ct);
        return Result.Success(id);
    }
}

[RequestHandler<ReplaceScenarioCommand, ScenarioView>]
public sealed class ReplaceScenarioHandler(
    IItemRegistry items, IItemRules rules, IScenarioStore store, ScenarioFactsReader facts, IAccessPolicy policy, IActorAccessor actor, IUnitOfWork unitOfWork)
    : IRequestHandler<ReplaceScenarioCommand, ScenarioView>
{
    public async ValueTask<Result<ScenarioView>> HandleAsync(ReplaceScenarioCommand command, CancellationToken ct = default)
    {
        var loaded = await ScenarioResults.LoadAsync(items, store, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure)) return Result.Failure<ScenarioView>(failure!);
        loaded.TryGetValue(out var found);
        // #294: an adult who may edit the scenario does not own it: its visibility stays with its owners.
        if (command.Visibility != found.Item.Visibility && !rules.CanCreate(actor.Current!.PersonId!.Value, found.Item.Owners))
            return Result.Failure<ScenarioView>(new UnauthorizedFailure("Only the scenario's owners change its visibility.", null));
        if (ScenarioResults.Name(command.Name) is { } unnamed) return Result.Failure<ScenarioView>(unnamed);
        var (refused, visible) = await facts.RefusalAsync(command.Document, command.Scope, ct);
        if (refused is not null) return Result.Failure<ScenarioView>(refused);

        // Spec §5: the writer was sent the document without the lines they cannot see; those lines are kept, not dropped.
        // No envelope twice: the refusal above turns down any envelope the writer cannot see.
        var document = command.Document with
        {
            Funding = [.. command.Document.Funding, .. found.Stored.Document!.Funding.Where(f => !visible.Contains(f.Item))],
        };
        var owners = found.Item.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList();
        var stored = found.Stored with { Scope = command.Scope, Document = document };
        await using (var transaction = await unitOfWork.BeginAsync(ct))
        {
            var replaced = await items.ReplaceAsync(command.ItemId, command.Name, command.Visibility, owners, ct);
            if (replaced.TryGetFailure(out failure)) return Result.Failure<ScenarioView>(failure!);
            await store.SaveAsync(stored, ct);
            await transaction.CommitAsync(ct);
        }
        var item = found.Item with { DisplayName = command.Name, Visibility = command.Visibility };
        return await ScenarioResults.ViewAsync(facts, policy, actor.Current!.PersonId!.Value, item, stored, ct);
    }
}

[RequestHandler<DeleteScenarioCommand, ItemId>]
public sealed class DeleteScenarioHandler(IItemRegistry items, IScenarioStore store, IClock clock) : IRequestHandler<DeleteScenarioCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(DeleteScenarioCommand command, CancellationToken ct = default)
    {
        // Only a scenario: this route never archives an item of another kind.
        var loaded = await ScenarioResults.LoadAsync(items, store, command.ItemId, ct, kind: null);
        if (loaded.TryGetFailure(out var failure)) return Result.Failure<ItemId>(failure!);
        var archived = await items.ArchiveAsync(command.ItemId, clock.GetCurrentInstant(), ct);
        return archived.TryGetFailure(out failure) ? Result.Failure<ItemId>(failure!) : Result.Success(command.ItemId);
    }
}

[RequestHandler<SimulateScenarioQuery, ScenarioResultView>]
public sealed class SimulateScenarioHandler(ScenarioFactsReader facts, IHouseholdCalendar calendar)
    : IRequestHandler<SimulateScenarioQuery, ScenarioResultView>
{
    public async ValueTask<Result<ScenarioResultView>> HandleAsync(SimulateScenarioQuery query, CancellationToken ct = default)
    {
        if (ScenarioFactsReader.Check(query.Document, await calendar.TodayAsync(ct)) is { } refused)
            return Result.Failure<ScenarioResultView>(refused);
        var read = await facts.ReadAsync(query.Document, query.Scope, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<ScenarioResultView>(failure!);
        read.TryGetValue(out var reading);
        return Result.Success(ScenarioResults.Of(reading!));
    }
}

[RequestHandler<GetScenarioDefaultsQuery, ScenarioDefaultsView>]
public sealed class GetScenarioDefaultsHandler(ScenarioFactsReader facts, IHouseholdSettings household) : IRequestHandler<GetScenarioDefaultsQuery, ScenarioDefaultsView>
{
    public async ValueTask<Result<ScenarioDefaultsView>> HandleAsync(GetScenarioDefaultsQuery query, CancellationToken ct = default)
    {
        var read = await facts.TodayAsync(query.Scope, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<ScenarioDefaultsView>(failure!);
        read.TryGetValue(out var today);
        var currency = today!.Reference;
        return Result.Success(new ScenarioDefaultsView(
            (await household.ProjectionAssumptionsAsync(ct)).ClassRates.ToDictionary(r => r.Key, r => PropertyPurchaseView.Rate(r.Value)),
            today.MonthlyIncome is { } income ? CapacityView.Money(income, currency) : null,
            CapacityView.Money(today.Commitments, currency),
            ScenarioResults.ByName(today.Savings.Where(s => s.Converted is not null), s => s.Holding.Name)
                .Select(s => new FundingCandidateView(s.Holding.Item, s.Holding.Name, CapacityView.Money(s.Converted!.Value, currency), s.Liquidity))
                .ToList()));
    }
}
