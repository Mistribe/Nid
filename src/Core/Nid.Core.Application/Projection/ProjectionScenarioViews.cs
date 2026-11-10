using System.Globalization;
using Nid.Core.Application.Scenarios;
using Nid.Core.Contracts;
using Nid.Core.Domain.Projection;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Application.Projection;

/// <summary>Spec §4: an event date on the wire, <c>{ on }</c> (ISO day) or <c>{ person, age }</c>.</summary>
public sealed record EventDateView(string? On, Guid? Person, int? Age);

/// <summary>Spec §6: a scenario's assumption overrides, as <see cref="ProjectionAssumptionsView"/> writes them; a null field keeps the household's.</summary>
public sealed record ProjectionOverridesView(
    IReadOnlyDictionary<string, string>? ClassRates, string? Inflation, string? MonthlySavings, string? SavingsClass, bool? FreedPaymentsToSavings,
    IReadOnlyDictionary<string, string>? InflationByYear = null, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? ClassRatesByYear = null,
    string? IncomeGrowth = null, IReadOnlyDictionary<string, string>? ClassVolatility = null, string? WithdrawalRate = null,
    string? MonthlyLivingCost = null);

/// <summary>
/// Spec §4: one event on the wire, the fields of <see cref="ProjectionEvent"/>; amounts are decimal strings in the reference currency.
/// An event sent without an id gets one.
/// </summary>
public sealed record ProjectionEventView(
    Guid? Id, string? Kind, EventDateView? Start, EventDateView? Until, string? Label, string? MonthlyAmount,
    string? Amount, string? Split, Guid? Loan, string? Effect, string? Penalty,
    Guid? Property, string? Price, string? Costs, Guid? Scenario);

/// <summary>Retirement spec §2: a departure on the wire; <c>MonthlyPension</c> a decimal string.</summary>
public sealed record ScenarioRetirementView(Guid? Person, int? Years, int? Months, string? MonthlyPension);

/// <summary>
/// Spec §6 and §7: a projection scenario's document on the wire, read and answered in the same shape. <c>Horizon</c> is the projection
/// route's form, <c>"10y"</c> or <c>"retire:{personId}"</c>. <see cref="Read"/> is the one wire → document mapping (routes and
/// assistant); <see cref="From"/> the one document → wire mapping.
/// </summary>
public sealed record ProjectionDocumentView(int Version, string? Horizon, ProjectionOverridesView? Assumptions, IReadOnlyList<ProjectionEventView>? Events,
    IReadOnlyList<ScenarioRetirementView>? Retirements = null)
{
    public static ProjectionDocumentView From(ProjectionScenario doc)
    {
        static string Amount(decimal v) => PropertyPurchaseView.Amount(v);
        static string? Optional(decimal? v) => v is { } d ? Amount(d) : null;
        static EventDateView Date(EventDate d) => new(d.On?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d.Person?.Value, d.Age);
        var o = doc.Assumptions;
        return new ProjectionDocumentView(
            doc.Version, new ProjectionHorizon(doc.Horizon.Years, doc.Horizon.RetireOf).ToWire(),
            new ProjectionOverridesView(
                o.ClassRates?.ToDictionary(r => r.Key, r => PropertyPurchaseView.Rate(r.Value)),
                o.Inflation is { } inflation ? PropertyPurchaseView.Rate(inflation) : null, Optional(o.MonthlySavings), o.SavingsClass,
                o.FreedPaymentsToSavings, ProjectionAssumptionsView.Years(o.InflationByYear),
                o.ClassRatesByYear?.Where(c => c.Value is not null).ToDictionary(c => c.Key, c => (IReadOnlyDictionary<string, string>)ProjectionAssumptionsView.Years(c.Value)!),
                o.IncomeGrowth is { } growth ? PropertyPurchaseView.Rate(growth) : null,
                o.ClassVolatility?.ToDictionary(v => v.Key, v => PropertyPurchaseView.Rate(v.Value)),
                o.WithdrawalRate is { } withdrawal ? PropertyPurchaseView.Rate(withdrawal) : null, Optional(o.MonthlyLivingCost)),
            doc.Events.Select(e => new ProjectionEventView(
                e.Id, e.Kind, Date(e.Start), e.Until is { } until ? Date(until) : null, e.Label, Optional(e.MonthlyAmount),
                Optional(e.Amount), e.Split, e.Loan?.Value, e.Effect, Optional(e.Penalty),
                e.Property?.Value, Optional(e.Price), Optional(e.Costs), e.Scenario?.Value)).ToList(),
            (doc.Retirements ?? []).Select(r => new ScenarioRetirementView(r.Person.Value, r.Years, r.Months, Optional(r.MonthlyPension))).ToList());
    }

    /// <summary>
    /// The document a body holds, or every unreadable field as <c>projection.invalid_field</c> with its path (<c>events[0].amount</c>).
    /// Only the reading: the document's rules are <see cref="ProjectionScenarioRefusals.Of"/>.
    /// </summary>
    public static Result<ProjectionScenario> Read(ProjectionDocumentView? view)
    {
        var errors = new List<FieldError>();
        void Refuse(string path, string message) => errors.Add(new FieldError(path, ProjectionCodes.InvalidField, message));

        decimal? Decimal(string? text, string path)
        {
            if (text is null) return null;
            if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                return value;
            Refuse(path, "An amount is a decimal string such as \"12.99\".");
            return null;
        }

        EventDate? Date(EventDateView? date, string path)
        {
            if (date is null) return null;
            NodaTime.LocalDate? on = null;
            if (date.On is not null)
            {
                if (LocalDatePattern.Iso.Parse(date.On) is { Success: true } parsed) on = parsed.Value;
                else Refuse($"{path}.on", $"'{date.On}' is not an ISO date (yyyy-mm-dd).");
            }
            return new EventDate(on, date.Person is { } person ? new PersonId(person) : null, date.Age);
        }

        if (view is null)
        {
            Refuse("document", "A document is required.");
            return Failed();
        }

        var horizon = ParseHorizon(view.Horizon);
        if (horizon is null) Refuse("horizon", "A horizon is '{n}y' or 'retire:{personId}'.");

        var a = view.Assumptions;
        var rates = new Dictionary<string, decimal>();
        foreach (var (@class, text) in a?.ClassRates ?? new Dictionary<string, string>())
            if (Decimal(text, $"assumptions.classRates.{@class}") is { } percent)
                rates[@class] = percent / 100m;
        var volatility = new Dictionary<string, decimal>();
        foreach (var (@class, text) in a?.ClassVolatility ?? new Dictionary<string, string>())
            if (Decimal(text, $"assumptions.classVolatility.{@class}") is { } percent)
                volatility[@class] = percent / 100m;
        var overrides = new ProjectionOverrides(
            a?.ClassRates is null ? null : rates, Decimal(a?.Inflation, "assumptions.inflation") / 100m,
            Decimal(a?.MonthlySavings, "assumptions.monthlySavings"), a?.SavingsClass, a?.FreedPaymentsToSavings,
            ProjectionAssumptionsView.Years(a?.InflationByYear, "assumptions.inflationByYear", Decimal, Refuse),
            ProjectionAssumptionsView.ClassYears(a?.ClassRatesByYear, "assumptions.classRatesByYear", Decimal, Refuse),
            Decimal(a?.IncomeGrowth, "assumptions.incomeGrowth") / 100m, a?.ClassVolatility is null ? null : volatility,
            Decimal(a?.WithdrawalRate, "assumptions.withdrawalRate") / 100m, Decimal(a?.MonthlyLivingCost, "assumptions.monthlyLivingCost"));

        var events = new List<ProjectionEvent>();
        for (var i = 0; i < (view.Events?.Count ?? 0); i++)
        {
            var e = view.Events![i];
            var at = $"events[{i}]";
            if (e is null)
            {
                Refuse(at, "An event is required.");
                continue;
            }
            events.Add(new ProjectionEvent(
                e.Id ?? Guid.NewGuid(), e.Kind ?? "", Date(e.Start, $"{at}.start")!, Date(e.Until, $"{at}.until"), e.Label,
                Decimal(e.MonthlyAmount, $"{at}.monthlyAmount"), Decimal(e.Amount, $"{at}.amount"), e.Split,
                e.Loan is { } loan ? new ItemId(loan) : null, e.Effect, Decimal(e.Penalty, $"{at}.penalty"),
                e.Property is { } property ? new ItemId(property) : null, Decimal(e.Price, $"{at}.price"), Decimal(e.Costs, $"{at}.costs"),
                e.Scenario is { } scenario ? new ItemId(scenario) : null));
        }

        var retirements = new List<ScenarioRetirement>();
        for (var i = 0; i < (view.Retirements?.Count ?? 0); i++)
        {
            var r = view.Retirements![i];
            var at = $"retirements[{i}]";
            if (r is null)
            {
                Refuse(at, "A departure is required.");
                continue;
            }
            if (r.Person is not { } person)
            {
                Refuse($"{at}.person", "A person is required.");
                continue;
            }
            retirements.Add(new ScenarioRetirement(new PersonId(person), r.Years, r.Months, Decimal(r.MonthlyPension, $"{at}.monthlyPension")));
        }

        return errors.Count > 0
            ? Failed()
            : Result.Success(new ProjectionScenario(view.Version, horizon!, overrides, events) { Retirements = retirements });

        Result<ProjectionScenario> Failed() => Result.Failure<ProjectionScenario>(
            FieldErrors.Many(ProjectionCodes.InvalidField, errors.Count == 1 ? errors[0].Message : "Some fields are not valid.", errors));
    }

    /// <summary><c>{n}y</c> (any number: the rules bound it) or <c>retire:{personId}</c>; null otherwise.</summary>
    private static ScenarioHorizon? ParseHorizon(string? horizon)
    {
        if (horizon is null) return null;
        if (horizon.EndsWith('y') && int.TryParse(horizon.AsSpan(0, horizon.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var years))
            return new ScenarioHorizon(years, null);
        if (horizon.StartsWith("retire:", StringComparison.Ordinal) && Guid.TryParse(horizon.AsSpan("retire:".Length), out var person))
            return new ScenarioHorizon(null, new PersonId(person));
        return null;
    }

    /// <summary>The items the document names: loans, properties and purchase scenarios.</summary>
    internal IEnumerable<ItemId> Targets()
        => (Events ?? []).SelectMany(e => new[] { e.Loan, e.Property, e.Scenario }).OfType<Guid>().Select(id => new ItemId(id));
}

/// <summary>Spec §8: the gap (scenario − base) at one point of the grid, nominal and in today's money, rounded to the cent.</summary>
public sealed record ProjectionGapView(LocalDate On, string Nominal, string Real);

/// <summary>
/// Spec §7: the base and the scenario on the same grid, the gap at each point and at the horizon (scenario − base, nominal and in
/// today's money), and the document as the reader may see it (spec §6: an event on an item they cannot see is left out).
/// <c>Gaps</c> follows the scenario's points; <c>GapNominal</c> and <c>GapReal</c> are its last entry.
/// </summary>
public sealed record ProjectionComparisonView(
    ProjectionView Base, ProjectionView Scenario, IReadOnlyList<ProjectionGapView> Gaps, string GapNominal, string GapReal, ProjectionDocumentView Document)
    : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Base.ExposedItems().Concat(Scenario.ExposedItems()).Concat(Document.Targets()).Distinct();

    /// <summary><paramref name="ranges"/>: the scenario's bands (ranges spec §4), the base's never.</summary>
    internal static ProjectionComparisonView From(ProjectionScenarioReading reading, bool ranges = false)
        => TryFrom(reading, ranges) ?? throw new InvalidOperationException("The scenario's grid differs from the base's, or is empty.");

    /// <summary>Null when the grids diverged or are empty, for a reader that answers without the comparison.</summary>
    public static ProjectionComparisonView? TryFrom(ProjectionScenarioReading reading, bool ranges = false)
    {
        var horizon = ProjectionHorizon.From(reading.VisibleDocument.Horizon);
        var based = HouseholdProjection.Project(reading.Base.Inputs);
        var scenario = HouseholdProjection.Project(reading.Scenario);
        // Both grids run from today to the same horizon: point i of one is point i of the other. Grids that diverged would make every
        // gap wrong, so they fail rather than being cut to the shorter.
        if (based.Points.Count == 0 || scenario.Points.Count != based.Points.Count || scenario.Points[^1].On != based.Points[^1].On)
            return null;
        var gaps = scenario.Points.Zip(based.Points, (s, b) => new ProjectionGapView(
            s.On, PropertyPurchaseView.Amount(s.Nominal - b.Nominal), PropertyPurchaseView.Amount(s.Real - b.Real))).ToList();
        return new ProjectionComparisonView(
            ProjectionView.From(horizon, reading.Base, based),
            ProjectionView.From(horizon, reading.Base with
            {
                Inputs = reading.Scenario, SavingsOrigin = reading.SavingsOrigin, ObservedInflation = reading.ObservedInflation,
            }, scenario, reading.ExtraWarnings, ranges ? ProjectionRanges.Simulate(reading.Scenario) : null),
            gaps, gaps[^1].Nominal, gaps[^1].Real,
            ProjectionDocumentView.From(reading.VisibleDocument));
    }
}

/// <summary>Spec §4.5 and §2.1: a document's refusals that need no read, each named by its field.</summary>
public static class ProjectionScenarioRefusals
{
    /// <summary>
    /// <see cref="ProjectionScenarioRules.Check"/>, then the assumptions the overrides give on <paramref name="household"/>'s, as
    /// <see cref="ProjectionRules.Check"/> bounds them (fields under <c>assumptions.</c>); null when the document is valid. The
    /// past years, the household's or the document's, are dropped first, never refused.
    /// </summary>
    public static Failure? Of(ProjectionScenario doc, ProjectionAssumptions household, LocalDate today)
    {
        var errors = ProjectionScenarioRules.Check(doc).ToList();
        if (doc.Assumptions is { } overrides)
            errors.AddRange(ProjectionRules.Check(ProjectionRules.WithoutPastYears(overrides.Apply(household), today), today)
                .Select(e => ($"assumptions.{e.Field}", e.Message)));
        return errors.Count == 0
            ? null
            : FieldErrors.Many(ProjectionCodes.InvalidField, errors.Count == 1 ? errors[0].Message : "Some fields are not valid.",
                errors.Select(e => new FieldError(e.Field, ProjectionCodes.InvalidField, e.Message)).ToList());
    }
}

/// <summary>
/// Spec §6 and §7: a saved projection scenario as the reader may see it; <c>Result</c> holds the base, the scenario, the gap at the
/// horizon and the document without the events on items the reader cannot see.
/// </summary>
public sealed record ProjectionScenarioView(ItemId Id, string Name, string Visibility, string Scope, bool CanWrite, ProjectionComparisonView Result)
    : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [Id, .. Result.ExposedItems()];
}
