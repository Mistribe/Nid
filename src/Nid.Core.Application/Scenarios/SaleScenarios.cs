using System.Globalization;
using Nid.Core.Application.Dashboard;
using Nid.Core.Application.Projection;
using Nid.Core.Application.Tax;
using Nid.Core.Contracts;
using Nid.Core.Domain.Scenarios;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Scenarios;

/// <summary>Issue #416: a sale document on the wire, amounts as invariant text; <c>Property</c> is null when the reader cannot see it.</summary>
public sealed record SaleScenarioDocView(int Version, ItemId? Property, LocalDate SaleOn, string? Price, string? AgencyFees, string? Penalties, int HorizonYears)
{
    public static SaleScenarioDocView From(SaleScenario d, bool seen) => new(
        d.Version, seen ? d.Property : null, d.SaleOn, PropertyPurchaseView.Amount(d.Price), PropertyPurchaseView.Amount(d.AgencyFees),
        PropertyPurchaseView.Amount(d.Penalties), d.HorizonYears);
}

/// <summary>
/// Issue #416: what the sale leaves the household on <c>SoldOn</c> (today when dated in the past). <c>NetSeller</c> = price − agency
/// fees − <c>Tax</c> − <c>LoansRepaid</c> − penalties; the monthly figures stop with the sale (<c>PropertyNet</c>: the charges less
/// the rent, negative when the rent was higher), <c>MonthlyChange</c> their sum.
/// </summary>
public sealed record SaleFiguresView(
    LocalDate SoldOn, DashboardMoneyView Price, DashboardMoneyView AgencyFees, DashboardMoneyView Tax, DashboardMoneyView LoansRepaid,
    DashboardMoneyView Penalties, DashboardMoneyView NetSeller, DashboardMoneyView LoanPayments, DashboardMoneyView PropertyNet,
    DashboardMoneyView MonthlyChange);

/// <summary>
/// Issue #416: the sale's figures (null when the reader cannot see the property), the capital gain as « Impôt si vente » reads it
/// (null likewise), and the net worth without and with the sale on the household's assumptions, as the purchase's points.
/// Warnings: <c>property_hidden</c>, <c>sale_in_past</c>, <c>tax_estimate_unavailable</c>.
/// </summary>
public sealed record SaleResultView(
    SaleFiguresView? Sale, SaleItemView? Tax, IReadOnlyList<ProjectionPointView> Points, DashboardMoneyView WithoutAtHorizon,
    DashboardMoneyView WithAtHorizon, DashboardMoneyView Gap, IReadOnlyList<ScenarioWarningView> Warnings) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Tax is { } tax ? [tax.Item] : [];
}

public sealed record SaleScenarioView(
    ItemId Id, string Name, string Visibility, string Scope, SaleScenarioDocView Document, SaleResultView Result, bool CanWrite) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [Id, .. Document.Property is { } p ? [p] : Array.Empty<ItemId>(), .. Result.ExposedItems()];
}

public sealed record SimulateSaleScenarioQuery(VisibilityScope Scope, SaleScenario Document) : IQuery<SaleResultView>;

public sealed record GetSaleScenarioQuery(ItemId ItemId) : IQuery<SaleScenarioView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

public sealed record CreateSaleScenarioCommand(
    string Name, Visibility Visibility, IReadOnlyList<OwnerShare> Owners, VisibilityScope Scope, SaleScenario Document)
    : ICommand<ItemId>, ICreatesItem;

/// <summary>Name, visibility, scope and document replaced whole; the owners are kept.</summary>
public sealed record ReplaceSaleScenarioCommand(ItemId ItemId, string Name, Visibility Visibility, VisibilityScope Scope, SaleScenario Document)
    : ICommand<SaleScenarioView>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>
/// Issue #416: a sale scenario read for the reader in a scope. The tax is the sale engine's on the property sold on the sale's date for
/// the price less the agency fees; the net worth is the projection's base against the base with the sale as its one event (costs: agency
/// fees, tax and penalties; its loans settled at their balance), which is also how « Comparer » reads it.
/// </summary>
public sealed class SaleScenarioReader(
    ProjectionScenarioReader projections, SaleTaxEstimates tax, IVisibleItems visible, IItemRegistry items, IActorAccessor actor,
    IHouseholdCalendar calendar)
{
    public const string PropertyNotFound = "scenario.property_not_found";

    private PersonId Reader => actor.Current!.PersonId!.Value;

    private sealed record Taxed(bool Seen, SaleItemView? Page, decimal Amount);

    /// <summary>The refusals of <see cref="ScenarioRules.Check(SaleScenario, LocalDate)"/>, located; null when valid.</summary>
    public static Failure? Check(SaleScenario doc, LocalDate today)
    {
        var errors = ScenarioRules.Check(doc, today);
        return errors.Count == 0
            ? null
            : FieldErrors.Many(ScenarioCodes.InvalidField, errors.Count == 1 ? errors[0].Message : "Some fields are not valid.",
                errors.Select(e => new FieldError(e.Field, ScenarioCodes.InvalidField, e.Message)).ToList());
    }

    /// <summary>On a write: the document's limits, then its property, an active one the writer sees in <paramref name="scope"/>.</summary>
    public async Task<Failure?> RefusalAsync(SaleScenario doc, VisibilityScope scope, CancellationToken ct)
        => Check(doc, await calendar.TodayAsync(ct))
           ?? (await SeesAsync(doc.Property, scope, ct)
               ? null
               : FieldErrors.At(new Failure(PropertyNotFound, "No such property, or not one you can see.", null), "property"));

    private async Task<bool> SeesAsync(ItemId property, VisibilityScope scope, CancellationToken ct)
    {
        if (!(await visible.ForAsync(Reader, scope, ct)).Contains(property)) return false;
        var found = await items.FindManyAsync([property], ct);
        return found.TryGetValue(property, out var access) && access is { ArchivedAt: null, Module: "realestate", Kind: "property" };
    }

    private async Task<Taxed> TaxAsync(SaleScenario doc, VisibilityScope scope, LocalDate today, CancellationToken ct)
    {
        if (!await SeesAsync(doc.Property, scope, ct)) return new(false, null, 0m);
        var on = doc.SaleOn < today ? today : doc.SaleOn;
        if (!(await tax.PropertyAsync(Reader, doc.Property, today, on, doc.Price - doc.AgencyFees, ct)).TryGetValue(out var page))
            return new(true, null, 0m);
        static decimal D(string? amount) => amount is null ? 0m : decimal.Parse(amount, CultureInfo.InvariantCulture);
        return new(true, page, D(page!.IncomeTax) + D(page.SocialTax) + D(page.Surtax));
    }

    /// <summary>The sale as a projection scenario's one event on the household's assumptions.</summary>
    private static ProjectionScenario Projection(SaleScenario doc, decimal tax, ScenarioHorizon horizon) => new(
        1, horizon, new ProjectionOverrides(null, null, null, null, null),
        [
            new ProjectionEvent(Guid.NewGuid(), ProjectionEventKinds.PropertySale, new EventDate(doc.SaleOn, null, null), null, null, null,
                null, null, null, null, null, doc.Property, doc.Price, ScenarioCalculator.SaleCosts(doc, tax), null),
        ]);

    /// <summary>« Comparer »: the sale as a projection scenario on the comparison's horizon and scope.</summary>
    public async Task<ProjectionScenario> ProjectionAsync(SaleScenario doc, VisibilityScope scope, ScenarioHorizon horizon, CancellationToken ct)
        => Projection(doc, (await TaxAsync(doc, scope, await calendar.TodayAsync(ct), ct)).Amount, horizon);

    public async Task<Result<SaleResultView>> ReadAsync(SaleScenario doc, VisibilityScope scope, CancellationToken ct)
    {
        var today = await calendar.TodayAsync(ct);
        var taxed = await TaxAsync(doc, scope, today, ct);
        var read = await projections.ReadAsync(Projection(doc, taxed.Amount, new ScenarioHorizon(doc.HorizonYears, null)), scope, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<SaleResultView>(failure!);
        read.TryGetValue(out var reading);
        // The grids diverged (or are empty): no figures to give, a failure the list reads as no gap.
        if (ProjectionComparisonView.TryFrom(reading!) is not { } comparison)
            return Result.Failure<SaleResultView>(new Failure("ERROR", "The sale's projection grid differs from the base's.", null));
        var currency = comparison.Base.Currency;
        DashboardMoneyView Money(decimal amount) => new(PropertyPurchaseView.Amount(amount), currency);

        var warnings = new List<ScenarioWarningView>();
        if (!taxed.Seen) warnings.Add(new("property_hidden", null));
        if (doc.SaleOn < today) warnings.Add(new("sale_in_past", null));
        if (taxed.Seen && taxed.Page is null or { Status: "finding" }) warnings.Add(new("tax_estimate_unavailable", taxed.Page?.Finding));

        SaleFiguresView? figures = null;
        if (taxed.Seen && reading!.Sales.FirstOrDefault() is { } outcome)
        {
            var r = ScenarioCalculator.Sale(doc, taxed.Amount, outcome);
            figures = new SaleFiguresView(
                doc.SaleOn < today ? today : doc.SaleOn, Money(doc.Price), Money(doc.AgencyFees), Money(r.Tax), Money(r.LoansRepaid),
                Money(doc.Penalties), Money(r.NetSeller), Money(r.LoanPayments), Money(r.PropertyNet), Money(r.MonthlyChange));
        }
        var points = comparison.Base.Points.Zip(comparison.Scenario.Points, (b, s) => new ProjectionPointView(
            b.On, new DashboardMoneyView(b.Nominal, currency), new DashboardMoneyView(s.Nominal, currency))).ToList();
        return Result.Success(new SaleResultView(
            figures, taxed.Page, points, points[^1].Without, points[^1].With, new DashboardMoneyView(comparison.GapNominal, currency), warnings));
    }

    public async Task<Result<SaleScenarioView>> ViewAsync(IAccessPolicy policy, ItemAccess item, StoredScenario stored, CancellationToken ct)
    {
        var read = await ReadAsync(stored.Sale!, stored.Scope, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<SaleScenarioView>(failure!);
        read.TryGetValue(out var result);
        var canWrite = (await policy.CheckAsync(Reader, item.Id, AccessKind.Write, ct)).IsSuccess;
        return Result.Success(new SaleScenarioView(
            item.Id, item.DisplayName, ScenarioResults.Wire(item.Visibility), stored.Scope == VisibilityScope.Me ? "me" : "household",
            SaleScenarioDocView.From(stored.Sale!, !result!.Warnings.Any(w => w.Code == "property_hidden")), result, canWrite));
    }
}

[RequestHandler<SimulateSaleScenarioQuery, SaleResultView>]
public sealed class SimulateSaleScenarioHandler(SaleScenarioReader reader, IHouseholdCalendar calendar)
    : IRequestHandler<SimulateSaleScenarioQuery, SaleResultView>
{
    public async ValueTask<Result<SaleResultView>> HandleAsync(SimulateSaleScenarioQuery query, CancellationToken ct = default)
        => SaleScenarioReader.Check(query.Document, await calendar.TodayAsync(ct)) is { } refused
            ? Result.Failure<SaleResultView>(refused)
            : await reader.ReadAsync(query.Document, query.Scope, ct);
}

[RequestHandler<GetSaleScenarioQuery, SaleScenarioView>]
public sealed class GetSaleScenarioHandler(IItemRegistry items, IScenarioStore store, SaleScenarioReader reader, IAccessPolicy policy)
    : IRequestHandler<GetSaleScenarioQuery, SaleScenarioView>
{
    public async ValueTask<Result<SaleScenarioView>> HandleAsync(GetSaleScenarioQuery query, CancellationToken ct = default)
    {
        var loaded = await ScenarioResults.LoadAsync(items, store, query.ItemId, ct, ScenarioKinds.PropertySale);
        if (loaded.TryGetFailure(out var failure)) return Result.Failure<SaleScenarioView>(failure!);
        loaded.TryGetValue(out var found);
        return await reader.ViewAsync(policy, found.Item, found.Stored, ct);
    }
}

[RequestHandler<CreateSaleScenarioCommand, ItemId>]
public sealed class CreateSaleScenarioHandler(IItemRegistry items, IScenarioStore store, SaleScenarioReader reader, IUnitOfWork unitOfWork)
    : IRequestHandler<CreateSaleScenarioCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(CreateSaleScenarioCommand command, CancellationToken ct = default)
    {
        if ((ScenarioResults.Name(command.Name) ?? await reader.RefusalAsync(command.Document, command.Scope, ct)) is { } refused)
            return Result.Failure<ItemId>(refused);

        // The Core item and the document row: one transaction, or an orphan item when the second write fails.
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var created = await items.CreateAsync(new NewItem("core", "scenario", command.Name, command.Visibility, null, command.Owners), ct);
        if (created.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(FieldErrors.At(failure!, "owners"));
        created.TryGetValue(out var id);
        await store.SaveAsync(new StoredScenario(id, ScenarioKinds.PropertySale, command.Scope, null, Sale: command.Document), ct);
        await transaction.CommitAsync(ct);
        return Result.Success(id);
    }
}

[RequestHandler<ReplaceSaleScenarioCommand, SaleScenarioView>]
public sealed class ReplaceSaleScenarioHandler(
    IItemRegistry items, IItemRules rules, IScenarioStore store, SaleScenarioReader reader, IAccessPolicy policy, IActorAccessor actor,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ReplaceSaleScenarioCommand, SaleScenarioView>
{
    public async ValueTask<Result<SaleScenarioView>> HandleAsync(ReplaceSaleScenarioCommand command, CancellationToken ct = default)
    {
        var loaded = await ScenarioResults.LoadAsync(items, store, command.ItemId, ct, ScenarioKinds.PropertySale);
        if (loaded.TryGetFailure(out var failure)) return Result.Failure<SaleScenarioView>(failure!);
        loaded.TryGetValue(out var found);
        // #294: an adult who may edit the scenario does not own it: its visibility stays with its owners.
        if (command.Visibility != found.Item.Visibility && !rules.CanCreate(actor.Current!.PersonId!.Value, found.Item.Owners))
            return Result.Failure<SaleScenarioView>(new UnauthorizedFailure("Only the scenario's owners change its visibility.", null));
        if ((ScenarioResults.Name(command.Name) ?? await reader.RefusalAsync(command.Document, command.Scope, ct)) is { } refused)
            return Result.Failure<SaleScenarioView>(refused);

        var owners = found.Item.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList();
        var stored = found.Stored with { Scope = command.Scope, Sale = command.Document };
        await using (var transaction = await unitOfWork.BeginAsync(ct))
        {
            var replaced = await items.ReplaceAsync(command.ItemId, command.Name, command.Visibility, owners, ct);
            if (replaced.TryGetFailure(out failure)) return Result.Failure<SaleScenarioView>(failure!);
            await store.SaveAsync(stored, ct);
            await transaction.CommitAsync(ct);
        }
        return await reader.ViewAsync(policy, found.Item with { DisplayName = command.Name, Visibility = command.Visibility }, stored, ct);
    }
}
