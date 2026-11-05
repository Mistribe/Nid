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
}

/// <summary>
/// Spec §4: turns a projection scenario's events into engine inputs, every read through the reader's visible items in the scope (spec §6).
/// An event dated before today is taken today, one past the horizon is ignored; an event on an item the reader cannot see, unknown or
/// archived is left out of the calculation and of the document, and counted.
/// </summary>
public sealed class ProjectionScenarioReader(
    ProjectionFactsReader facts, ScenarioFactsReader purchases, IVisibleItems visible, IItemRegistry items, IScenarioStore store,
    IPersonRegistry persons, IActorAccessor actor, IHouseholdSettings household, IRealEstateProjection realEstate, IHouseholdCalendar calendar)
{
    public const string EventMilestone = "event";
    public const string ScenarioSavings = "scenario";

    private PersonId Reader => actor.Current!.PersonId!.Value;

    /// <summary>
    /// Spec §6: what the reader sees of a document's targets in the scope — items active and of the kind the event names, purchase
    /// scenarios stored as such, persons of the household.
    /// </summary>
    private sealed record Targets(
        Func<ItemId?, string, string, bool> Sees, IReadOnlyDictionary<ItemId, StoredScenario> Purchases, IReadOnlyDictionary<PersonId, PersonInfo> People)
    {
        public bool Loan(ItemId? id) => Sees(id, "realestate", "loan");

        public bool Property(ItemId? id) => Sees(id, "realestate", "property");

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
        bool Sees(ItemId? id, string module, string kind)
            => id is { } item && seen.Contains(item) && accesses.TryGetValue(item, out var access)
               && access is { ArchivedAt: null } && access.Module == module && access.Kind == kind;
        var stored = (await store.FindManyAsync(named.Where(id => Sees(id, "core", "scenario")).ToList(), ct))
            .Where(s => s is { Kind: ScenarioKinds.PropertyPurchase, Document: not null })
            .ToDictionary(s => s.Item);
        var people = (await persons.ListAsync(ct)).ToDictionary(p => p.Id);
        return new Targets(Sees, stored, people);
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
        bool Active(ItemId? id, string module, string kind)
            => id is { } item && accesses.TryGetValue(item, out var access) && access is { ArchivedAt: null } && access.Module == module && access.Kind == kind;
        var purchases = (await store.FindManyAsync(ids.Where(id => Active(id, "core", "scenario")).ToList(), ct))
            .Where(s => s is { Kind: ScenarioKinds.PropertyPurchase, Document: not null }).ToDictionary(s => s.Item);
        var alive = new Targets(Active, purchases, targets.People);
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
        if (ProjectionScenarioRefusals.Of(doc, await household.ProjectionAssumptionsAsync(ct)) is { } refused) return refused;
        var today = await calendar.TodayAsync(ct);
        var span = await facts.MonthsAsync(ProjectionHorizon.From(doc.Horizon), today, ct);
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

    public async Task<Result<ProjectionScenarioReading>> ReadAsync(ProjectionScenario doc, VisibilityScope scope, CancellationToken ct)
    {
        var horizon = ProjectionHorizon.From(doc.Horizon);
        var read = await facts.ReadAsync(scope, horizon, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<ProjectionScenarioReading>(failure!);
        read.TryGetValue(out var based);
        var today = based!.Inputs.Today;
        var months = based.Inputs.Months;
        var thisMonth = today.ToYearMonth();
        var assumptions = doc.Assumptions.Apply(await household.ProjectionAssumptionsAsync(ct));

        var targets = await TargetsAsync(doc, scope, ct);
        var stored = targets.Purchases;
        var people = targets.People;
        int Month(LocalDate on) => (on.Year - today.Year) * 12 + on.Month - today.Month;

        var adjustments = new decimal[months + 1];
        var cashFlows = new decimal[months + 1];
        var withdrawals = new List<SliceWithdrawal>();
        var repayments = new List<ProjectedRepayment>();
        var penalties = new Dictionary<Guid, decimal>();
        var sales = new List<ProjectedSale>();
        var bought = new List<(PropertyPurchase Document, IReadOnlyList<SimulatedLoan> Loans, int Month)>();
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
                bought.Add((reading.Document, reading.Facts.Loans, Month(purchaseOn)));
                AddPurchase(reading, Month(purchaseOn));
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
                    // [start, until): an end on or before the start is an empty interval.
                    var to = until is { } stop ? Math.Clamp(Month(stop), 0, months + 1) : months + 1;
                    for (var k = Month(start); k < to; k++) adjustments[k] += e.MonthlyAmount ?? 0m;
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

        var scenarioRead = await facts.ReadAsync(scope, horizon, assumptions, new RealEstateEvents(repayments, sales), ct);
        if (scenarioRead.TryGetFailure(out failure)) return Result.Failure<ProjectionScenarioReading>(failure!);
        scenarioRead.TryGetValue(out var scenario);
        var realEstate = scenario!.RealEstate;

        // Spec §4.2: what the loans took and the penalty leave the savings in the repayment's month; nothing when no loan took any.
        foreach (var repayment in realEstate.Repayments.Where(r => r.Applied > 0m))
            cashFlows[repayment.Month] -= repayment.Applied + penalties.GetValueOrDefault(repayment.Event);
        // Spec §4.3: the proceeds enter in the sale's month; its charges less its rent are saved from the month after.
        foreach (var sale in realEstate.Sales)
        {
            cashFlows[sale.Month] += sale.Proceeds;
            for (var k = sale.Month + 1; k <= months; k++) adjustments[k] += sale.MonthlyNet;
        }

        // Spec §4.4: the purchased homes and their loans' balances join the real estate (value and debt; their payments are in the
        // monthly cost, so they are not freed payments).
        var growth = (double)assumptions.ClassRates.GetValueOrDefault(AllocationClasses.RealEstate);
        var grid = scenario.Inputs.RealEstate.Count == 0
            ? Enumerable.Range(0, months + 1).Select(k => new RealEstateMonth(thisMonth.PlusMonths(k), 0m, 0m, 0m)).ToList()
            : scenario.Inputs.RealEstate.ToList();
        var property = grid.Select((m, k) =>
        {
            var value = 0m;
            var debt = 0m;
            var end = m.Month.ToDateInterval().End;
            foreach (var (document, loans, from) in bought.Where(b => b.Month <= k))
            {
                value += document.Price * (decimal)Math.Pow(1 + growth, (k - from) / 12.0);
                debt += document.Loans.Zip(loans, (l, s) => s.Rows.LastOrDefault(r => r.DueDate <= end) is { } row ? row.Balance : l.Amount).Sum();
            }
            return m with { PropertyValue = m.PropertyValue + value, Debt = m.Debt + debt };
        }).ToList();
        // Spec §3.3: the payments freed are counted from today's, before any event. RealEstate applies an event of this month (or
        // taken today) to month 0, so the scenario's own month 0 may already be without a sold loan's payment or with a reduced one.
        if (based.Inputs.RealEstate.Count > 0)
            property[0] = property[0] with { LoanPayments = based.Inputs.RealEstate[0].LoanPayments };

        var warnings = new List<ProjectionWarningView>();
        void Count(string code, int count)
        {
            if (count > 0) warnings.Add(new ProjectionWarningView(code, count.ToString(CultureInfo.InvariantCulture)));
        }
        Count("hidden_targets", hidden);
        Count("event_in_past", past);
        if (purchaseInPast) warnings.Add(new ProjectionWarningView("purchase_in_past", null));
        Count("event_undated", undated);
        if (taxUnavailable) warnings.Add(new ProjectionWarningView("tax_estimate_unavailable", null));

        var inputs = scenario.Inputs with
        {
            RealEstate = property, MonthlyAdjustments = adjustments, CashFlows = cashFlows, Withdrawals = withdrawals,
            Milestones = [.. scenario.Inputs.Milestones, .. milestones],
        };
        return Result.Success(new ProjectionScenarioReading(
            based, inputs, doc with { Events = kept }, warnings, milestones,
            doc.Assumptions.MonthlySavings is null ? based.SavingsOrigin : ScenarioSavings) { Sales = realEstate.Sales });

        PersonInfo? Person(PersonId id) => people.GetValueOrDefault(id);

        // Spec §4.4, as ScenarioCalculator.Project: the down payment leaves its envelopes in the purchase's month; from then each month
        // costs its loans' due payments and insurance, the charges and the tax increase, less the rent it brings or saves.
        void AddPurchase(ScenarioReading reading, int month)
        {
            var purchase = reading.Document;
            withdrawals.AddRange(purchase.Funding.Select(f => new SliceWithdrawal(month, f.Item, f.Amount)));
            var rent = purchase.Use == PurchaseUse.Rental ? purchase.ExpectedRent * (12 - purchase.VacancyMonths) / 12 : purchase.CurrentRent;
            var monthlyCost = purchase.MonthlyCharges - rent + reading.Facts.MonthlyTaxIncrease;
            var rows = reading.Facts.Loans.SelectMany(l => l.Rows).ToList();
            for (var k = month; k <= months; k++)
            {
                var due = thisMonth.PlusMonths(k);
                adjustments[k] -= rows.Where(r => r.DueDate.ToYearMonth() == due).Sum(r => r.Payment + r.Insurance) + monthlyCost;
            }
        }
    }
}
