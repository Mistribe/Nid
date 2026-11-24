using System.Globalization;
using Nid.Core.Application.Scenarios;
using Nid.Core.Contracts;
using Nid.Core.Domain.Projection;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Application.Projection;

/// <summary>
/// Spec §4 and §6: the base (<see cref="ProjectionFactsReader"/>, household assumptions, no event) and the scenario's engine inputs, on
/// the same grid. <c>VisibleDocument</c> is the document without the events on items the reader cannot see; <c>ExtraWarnings</c> the
/// scenario's own (spec §3.4 order); <c>EventMilestones</c> one per event applied within the grid; <c>SavingsOrigin</c> is the scenario's
/// S₀ origin (<c>"scenario"</c> when it sets its own). <c>Sales</c> are the outcomes of the sales applied within the grid.
/// </summary>
public sealed record ProjectionScenarioReading(
    ProjectionReading Base, ProjectionInputs Scenario, ProjectionScenario VisibleDocument, IReadOnlyList<ProjectionWarningView> ExtraWarnings,
    IReadOnlyList<ProjectionMilestone> EventMilestones, string SavingsOrigin)
{
    public IReadOnlyList<SaleOutcome> Sales { get; init; } = [];

    /// <summary>The observed inflation the scenario's own assumptions use for the current year (see <see cref="ProjectionReading"/>).</summary>
    public decimal? ObservedInflation { get; init; }
}

/// <summary>
/// Spec §4: turns a projection scenario's events into engine inputs, every read through the reader's visible items in the scope (spec §6).
/// An event dated before today is taken today, one past the horizon is ignored; an event on an item the reader cannot see, unknown or
/// archived is left out of the calculation and of the document, and counted.
/// </summary>
public sealed class ProjectionScenarioReader(
    ProjectionFactsReader facts, ScenarioFactsReader purchases, IVisibleItems visible, IItemRegistry items, IScenarioStore store,
    IPersonRegistry persons, IActorAccessor actor, IHouseholdSettings household, IRealEstateProjection realEstate, IRealEstateItems realEstateItems,
    IHouseholdCalendar calendar)
{
    public const string EventMilestone = "event";
    public const string ScenarioSavings = "scenario";

    private PersonId Reader => actor.Current!.PersonId!.Value;

    private static bool IsScenario(ItemAccess item) => item is { Module: "core", Kind: "scenario" };

    /// <summary>
    /// Spec §6: what the reader sees of a document's targets in the scope — items active and of the kind the event names, purchase
    /// scenarios stored as such, persons of the household.
    /// </summary>
    private sealed record Targets(
        Func<ItemId?, Func<ItemAccess, bool>, bool> Sees, IRealEstateItems RealEstate, IReadOnlyDictionary<ItemId, StoredScenario> Purchases,
        IReadOnlyDictionary<PersonId, PersonInfo> People)
    {
        public bool Loan(ItemId? id) => Sees(id, RealEstate.IsLoan);

        public bool Property(ItemId? id) => Sees(id, RealEstate.IsProperty);

        public bool Purchase(ItemId? id) => id is { } s && Purchases.ContainsKey(s);

        public bool Person(PersonId? id) => id is not { } person || People.ContainsKey(person);

        /// <summary>The event's item is seen, and so are its persons (a purchase's start is ignored: its scenario's date counts).</summary>
        public bool Visible(ProjectionEvent e)
            => e.Kind switch
               {
                   ProjectionEventKinds.EarlyRepayment => e.Split != ProjectionSplits.Chosen || Loan(e.Loan),
                   ProjectionEventKinds.PropertySale => Property(e.Property),
                   ProjectionEventKinds.PropertyPurchase => Purchase(e.Scenario),
                   _ => true,
               }
               && (e.Kind == ProjectionEventKinds.PropertyPurchase || Person(e.Start.Person)) && Person(e.Until?.Person);
    }

    private async Task<Targets> TargetsAsync(ProjectionScenario doc, VisibilityScope scope, CancellationToken ct)
    {
        var seen = await visible.ForAsync(Reader, scope, ct);
        var named = doc.Events.SelectMany(e => new[] { e.Loan, e.Property, e.Scenario }).OfType<ItemId>().Where(seen.Contains).Distinct().ToList();
        var accesses = await items.FindManyAsync(named, ct);
        bool Sees(ItemId? id, Func<ItemAccess, bool> of)
            => id is { } item && seen.Contains(item) && accesses.TryGetValue(item, out var access) && access is { ArchivedAt: null } && of(access);
        var stored = (await store.FindManyAsync(named.Where(id => Sees(id, IsScenario)).ToList(), ct))
            .Where(s => s is { Kind: ScenarioKinds.PropertyPurchase, Document: not null })
            .ToDictionary(s => s.Item);
        var people = (await persons.ListAsync(ct)).ToDictionary(p => p.Id);
        return new Targets(Sees, realEstateItems, stored, people);
    }

    /// <summary>
    /// Spec §6: the events of a stored document the reader was not shown because they cannot see its target in <paramref name="scope"/>,
    /// with their index; a replace keeps them. An event nobody can see any more — its loan, property or purchase scenario archived or
    /// gone, a person no longer of the household — is not kept: the replace drops it, so the writer can be rid of it.
    /// </summary>
    public async Task<IReadOnlyList<(int Index, ProjectionEvent Event)>> HiddenAsync(ProjectionScenario doc, VisibilityScope scope, CancellationToken ct)
    {
        var targets = await TargetsAsync(doc, scope, ct);
        var hidden = doc.Events.Select((e, i) => (Index: i, Event: e)).Where(x => !targets.Visible(x.Event)).ToList();
        if (hidden.Count == 0) return hidden;

        // A registry read, whoever may see the items: does each target still exist, active and of its kind?
        var ids = hidden.SelectMany(x => new[] { x.Event.Loan, x.Event.Property, x.Event.Scenario }).OfType<ItemId>().Distinct().ToList();
        var accesses = await items.FindManyAsync(ids, ct);
        bool Active(ItemId? id, Func<ItemAccess, bool> of)
            => id is { } item && accesses.TryGetValue(item, out var access) && access is { ArchivedAt: null } && of(access);
        var purchases = (await store.FindManyAsync(ids.Where(id => Active(id, IsScenario)).ToList(), ct))
            .Where(s => s is { Kind: ScenarioKinds.PropertyPurchase, Document: not null }).ToDictionary(s => s.Item);
        var alive = new Targets(Active, realEstateItems, purchases, targets.People);
        return hidden.Where(x => alive.Visible(x.Event)).ToList();
    }

    private static FieldError Late(string path)
        => new(path, ProjectionCodes.InvalidField, $"A date is at most {ProjectionScenarioRules.MaxYears} years from now.");

    /// <summary>
    /// Spec §4.5: a written document's refusals, each named by its field. Its shape and the assumptions (<see cref="ProjectionScenarioRefusals"/>),
    /// the horizon; then every person, loan, property and purchase scenario it names, seen by the writer in <paramref name="scope"/> and
    /// active (<c>projection.target_not_found</c>); then a chosen loan whose property an earlier event sells (<c>events[i].loan</c>) and a
    /// repayment above what the loans can take at its date (<c>events[i].amount</c>, as the loan simulation), RealEstate projecting to the
    /// last event's month. An event whose person lost their birth date is not dated, so not checked against the loans. Null when valid.
    /// </summary>
    public async Task<Failure?> RefusalAsync(ProjectionScenario doc, VisibilityScope scope, CancellationToken ct)
    {
        var today = await calendar.TodayAsync(ct);
        if (ProjectionScenarioRefusals.Of(doc, await household.ProjectionAssumptionsAsync(ct), today) is { } refused) return refused;
        var span = await facts.MonthsAsync(ProjectionHorizon.From(doc.Horizon), today, ct, (await DeparturesAsync(doc, ct)).Ages);
        if (span.TryGetFailure(out var failure)) return FieldErrors.At(failure!, "horizon");

        // RealEstate projects to the last event's month: a day far in the future would project thousands of months.
        var latest = today.PlusYears(ProjectionScenarioRules.MaxYears).PlusMonths(1);
        var late = new List<FieldError>();
        for (var i = 0; i < doc.Events.Count; i++)
        {
            if (doc.Events[i].Start.On > latest) late.Add(Late($"events[{i}].start.on"));
            if (doc.Events[i].Until?.On > latest) late.Add(Late($"events[{i}].until.on"));
        }
        if (late.Count > 0)
            return FieldErrors.Many(ProjectionCodes.InvalidField, late.Count == 1 ? late[0].Message : "Some fields are not valid.", late);

        var targets = await TargetsAsync(doc, scope, ct);
        var missing = new List<FieldError>();
        void Missing(string path) => missing.Add(new FieldError(path, ProjectionCodes.TargetNotFound, "No such item, or not one you can see."));
        for (var i = 0; i < doc.Events.Count; i++)
        {
            var e = doc.Events[i];
            var at = $"events[{i}]";
            if (e.Kind == ProjectionEventKinds.EarlyRepayment && e.Split == ProjectionSplits.Chosen && !targets.Loan(e.Loan)) Missing($"{at}.loan");
            if (e.Kind == ProjectionEventKinds.PropertySale && !targets.Property(e.Property)) Missing($"{at}.property");
            if (e.Kind == ProjectionEventKinds.PropertyPurchase && !targets.Purchase(e.Scenario)) Missing($"{at}.scenario");
            // A purchase's start is not read (its scenario's date counts), as in the calculation.
            if (e.Kind != ProjectionEventKinds.PropertyPurchase && !targets.Person(e.Start.Person)) Missing($"{at}.start.person");
            if (!targets.Person(e.Until?.Person)) Missing($"{at}.until.person");
        }
        for (var i = 0; i < (doc.Retirements?.Count ?? 0); i++)
            if (!targets.People.TryGetValue(doc.Retirements![i].Person, out var retiree) || retiree.Kind != PersonKind.Adult)
                Missing($"retirements[{i}].person");
        if (missing.Count > 0)
            return FieldErrors.Many(ProjectionCodes.TargetNotFound, missing.Count == 1 ? missing[0].Message : "Some items are not found.", missing);

        // The dated repayments and sales, taken today when past; RealEstate applies them in date order, a repayment before a sale on
        // the same day.
        LocalDate? On(ProjectionEvent e)
            => ProjectionScenarioRules.Resolve(e.Start, id => targets.People.GetValueOrDefault(id)) is { } on ? (on < today ? today : on) : null;
        var repayments = doc.Events.Select((e, i) => (Index: i, Event: e, On: On(e)))
            .Where(x => x.Event.Kind == ProjectionEventKinds.EarlyRepayment && x.On is not null).ToList();
        if (repayments.Count == 0) return null;
        var sales = doc.Events.Select(e => (Event: e, On: On(e)))
            .Where(x => x.Event.Kind == ProjectionEventKinds.PropertySale && x.On is not null).ToList();
        var last = repayments.Select(r => r.On!.Value).Concat(sales.Select(s => s.On!.Value)).Max();
        var months = (last.Year - today.Year) * 12 + last.Month - today.Month;
        var projected = await realEstate.ProjectAsync(Reader, scope, today, months, 0m, new RealEstateEvents(
            repayments.Select(r => new ProjectedRepayment(r.Event.Id, r.On!.Value, r.Event.Amount ?? 0m, r.Event.Split!, r.Event.Loan, r.Event.Effect!)).ToList(),
            sales.Select(s => new ProjectedSale(s.Event.Id, s.Event.Property!.Value, s.On!.Value, s.Event.Price, s.Event.Costs ?? 0m)).ToList()), ct);
        if (projected.TryGetFailure(out failure)) return failure;
        projected.TryGetValue(out var projection);

        var invalid = new List<FieldError>();
        var properties = projection!.LoanEnds.GroupBy(l => l.Loan).ToDictionary(g => g.Key, g => g.First().Property);
        var applied = projection.Repayments.ToDictionary(r => r.Event, r => r.Applied);
        foreach (var (index, e, on) in repayments)
        {
            if (e.Split == ProjectionSplits.Chosen && properties.GetValueOrDefault(e.Loan!.Value) is { } property
                && sales.Any(s => s.Event.Property == property && s.On < on))
                invalid.Add(new FieldError($"events[{index}].loan", ProjectionCodes.InvalidField, "This loan's property is sold before the repayment."));
            else if (applied.GetValueOrDefault(e.Id) < e.Amount)
                invalid.Add(new FieldError($"events[{index}].amount", ProjectionCodes.InvalidField, "Above what the loans still owe at this date."));
        }
        return invalid.Count == 0
            ? null
            : FieldErrors.Many(ProjectionCodes.InvalidField, invalid.Count == 1 ? invalid[0].Message : "Some fields are not valid.", invalid);
    }

    /// <summary>
    /// Retirement spec §2: the document's departures on adults of the household, as ages; <c>Undated</c> counts those on a person
    /// without a birth date, which the calculation ignores. Persons are household-wide, so none is hidden.
    /// </summary>
    private async Task<(IReadOnlyDictionary<PersonId, (int Years, int Months)> Ages, IReadOnlyList<PersonInfo> People, int Undated, IReadOnlyList<ScenarioRetirement> Kept)> DeparturesAsync(
        ProjectionScenario doc, CancellationToken ct)
    {
        var all = (await persons.ListAsync(ct)).ToDictionary(p => p.Id);
        var kept = (doc.Retirements ?? []).Where(r => all.TryGetValue(r.Person, out var p) && p.Kind == PersonKind.Adult).ToList();
        var dated = kept.Where(r => all[r.Person].BirthDate is not null).ToList();
        var ages = dated.Where(r => r.Years is not null).ToDictionary(r => r.Person, r => (r.Years!.Value, r.Months ?? 0));
        var people = ProjectionFactsReader.Overridden([.. all.Values], ages);
        return (ages, people, kept.Count - dated.Count, kept);
    }

    public Task<Result<ProjectionScenarioReading>> ReadAsync(ProjectionScenario doc, VisibilityScope scope, CancellationToken ct)
        => ReadAsync(doc, scope, false, ct);

    /// <summary>
    /// <paramref name="householdHorizon"/>: a <c>retire:</c> horizon resolves with the household's departures, not the scenario's
    /// (Compare: every scenario shares the base's grid).
    /// </summary>
    public async Task<Result<ProjectionScenarioReading>> ReadAsync(ProjectionScenario doc, VisibilityScope scope, bool householdHorizon, CancellationToken ct)
    {
        var horizon = ProjectionHorizon.From(doc.Horizon);
        var departed = await DeparturesAsync(doc, ct);
        // One grid: the base reads the household's departures, on the scenario's horizon.
        var gridDepartures = householdHorizon ? null : departed.Ages;
        var read = await facts.ReadAsync(scope, horizon, null, RealEstateEvents.None, null, gridDepartures, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<ProjectionScenarioReading>(failure!);
        read.TryGetValue(out var based);
        var today = based!.Inputs.Today;
        doc = ProjectionScenarios.WithoutPastYears(doc, today); // a stored past year is neither used nor echoed
        var months = based.Inputs.Months;
        var assumptions = doc.Assumptions.Apply(await household.ProjectionAssumptionsAsync(ct));

        var targets = await TargetsAsync(doc, scope, ct);
        var stored = targets.Purchases;
        var people = targets.People;
        int Month(LocalDate on) => (on.Year - today.Year) * 12 + on.Month - today.Month;

        var changes = new List<ScheduledChange>();
        var repayments = new List<ProjectedRepayment>();
        var penalties = new Dictionary<Guid, decimal>();
        var sales = new List<ProjectedSale>();
        var bought = new List<ScenarioPurchase>();
        var milestones = new List<ProjectionMilestone>();
        var kept = new List<ProjectionEvent>();
        int hidden = 0, past = 0, undated = 0;
        bool purchaseInPast = false, taxUnavailable = false;

        foreach (var e in doc.Events)
        {
            if (!targets.Visible(e))
            {
                hidden++;
                continue;
            }
            kept.Add(e);
            ProjectionMilestone Milestone(LocalDate on, ItemId? item) => new(on, EventMilestone, e.Label ?? e.Kind, item);

            if (e.Kind == ProjectionEventKinds.PropertyPurchase)
            {
                // Spec §4.4: the purchase's own date counts, the event's is ignored; its funding lines the reader cannot see are hidden.
                var purchase = stored[e.Scenario!.Value].Document!;
                var purchaseRead = await purchases.ReadAsync(purchase, scope, ct);
                if (purchaseRead.TryGetFailure(out failure)) return Result.Failure<ProjectionScenarioReading>(failure!);
                purchaseRead.TryGetValue(out var reading);
                hidden += reading!.HiddenFunding;
                var purchaseOn = purchase.PurchaseOn;
                if (purchaseOn < today)
                {
                    purchaseOn = today;
                    purchaseInPast = true;
                }
                if (purchaseOn > based.HorizonOn) continue;
                taxUnavailable |= reading.TaxUnavailable;
                bought.Add(new ScenarioPurchase(Month(purchaseOn), reading.Document, reading.Facts.Loans, reading.Facts.MonthlyTaxIncrease));
                milestones.Add(Milestone(purchaseOn, e.Scenario));
                continue;
            }

            var resolved = ProjectionScenarioRules.Resolve(e.Start, Person);
            LocalDate? until = e.Until is { } end ? ProjectionScenarioRules.Resolve(end, Person) : null;
            if (resolved is not { } start || e.Until is not null && until is null)
            {
                undated++;
                continue;
            }
            if (start < today)
            {
                past++;
                start = today;
            }
            if (start > based.HorizonOn) continue;

            switch (e.Kind)
            {
                case ProjectionEventKinds.IncomeChange or ProjectionEventKinds.SavingsChange:
                    // [start, until): an end on or before the start is an empty interval. The amount is in money of its start month
                    // (inflation spec C17), indexed by the engine from there.
                    var to = until is { } stop ? Math.Clamp(Month(stop), 0, months + 1) : months + 1;
                    changes.Add(new ScheduledChange(Month(start), to, e.MonthlyAmount ?? 0m, e.Kind == ProjectionEventKinds.IncomeChange));
                    break;
                case ProjectionEventKinds.EarlyRepayment:
                    repayments.Add(new ProjectedRepayment(e.Id, start, e.Amount ?? 0m, e.Split!, e.Loan, e.Effect!));
                    penalties[e.Id] = e.Penalty ?? 0m;
                    break;
                case ProjectionEventKinds.PropertySale:
                    sales.Add(new ProjectedSale(e.Id, e.Property!.Value, start, e.Price, e.Costs ?? 0m));
                    break;
            }
            milestones.Add(Milestone(start, e.Loan ?? e.Property));
        }

        var scenarioRead = await facts.ReadAsync(scope, horizon, assumptions, new RealEstateEvents(repayments, sales), departed.Ages, gridDepartures, ct);
        if (scenarioRead.TryGetFailure(out failure)) return Result.Failure<ProjectionScenarioReading>(failure!);
        scenarioRead.TryGetValue(out var scenario);
        var realEstate = scenario!.RealEstate;

        var warnings = new List<ProjectionWarningView>();
        void Count(string code, int count)
        {
            if (count > 0) warnings.Add(new ProjectionWarningView(code, count.ToString(CultureInfo.InvariantCulture)));
        }
        Count("hidden_targets", hidden);
        Count("event_in_past", past);
        if (purchaseInPast) warnings.Add(new ProjectionWarningView("purchase_in_past", null));
        Count("event_undated", undated);
        Count("retirement_undated", departed.Undated);
        if (taxUnavailable) warnings.Add(new ProjectionWarningView("tax_estimate_unavailable", null));

        // Retirement spec §2: the scenario's pension of each adult who has one, from their switch month, in place of their own.
        var pensions = Departures.SwitchMonths(departed.People, today)
            .Select(x => (Switch: x, Kept: departed.Kept.FirstOrDefault(r => r.Person == x.Key)))
            .Where(x => x.Kept is { MonthlyPension: not null })
            .Select(x => new ScenarioPension(x.Switch.Value, x.Kept!.MonthlyPension!.Value, scenario.PensionsByHolder.GetValueOrDefault(x.Switch.Key)))
            .ToList();
        var inputs = ScenarioEvents.Apply(scenario.Inputs, today, months, based.Inputs.RealEstate, realEstate, penalties, bought,
            assumptions.ClassRates.GetValueOrDefault(AllocationClasses.RealEstate), pensions, changes, milestones);
        return Result.Success(new ProjectionScenarioReading(
            based, inputs, doc with { Events = kept, Retirements = departed.Kept }, warnings, milestones,
            doc.Assumptions.MonthlySavings is null ? based.SavingsOrigin : ScenarioSavings)
        {
            Sales = realEstate.Sales, ObservedInflation = scenario.ObservedInflation,
        });

        PersonInfo? Person(PersonId id) => people.GetValueOrDefault(id);
    }
}
