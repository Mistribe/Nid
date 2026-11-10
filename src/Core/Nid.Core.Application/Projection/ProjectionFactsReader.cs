using Nid.Core.Application.Dashboard;
using Nid.Core.Application.Scenarios;
using Nid.Core.Contracts;
using Nid.Core.Domain.Projection;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Application.Projection;

/// <summary>
/// Projection spec §2.3: <c>Years</c> years from this month, or to the retirement of <c>RetireOf</c>; exactly one is set. The wire
/// form is <c>"10y"</c> or <c>"retire:{personId}"</c>.
/// </summary>
public sealed record ProjectionHorizon(int? Years, PersonId? RetireOf)
{
    public const int MaxYears = 40;

    public static readonly ProjectionHorizon Default = new(10, null);

    public string ToWire() => RetireOf is { } person ? $"retire:{person.Value}" : $"{Years}y";

    /// <summary>A scenario's stored horizon; a retirement follows the same rules as the route's.</summary>
    public static ProjectionHorizon From(ScenarioHorizon horizon) => new(horizon.Years, horizon.RetireOf);
}

/// <summary>
/// What the base projection reads (spec §3.2): <c>Inputs</c> for <see cref="HouseholdProjection"/>, the horizon's last day, where
/// S₀ comes from (<c>"auto"</c> or <c>"household"</c>), the income sources carried on, the currencies left out (ordinal order), and
/// the retirements a horizon may reach.
/// </summary>
public sealed record ProjectionReading(
    ProjectionInputs Inputs, Currency Reference, LocalDate HorizonOn, string SavingsOrigin, IReadOnlyList<ExtendedSource> Extended,
    IReadOnlyList<string> Missing, IReadOnlyList<RetirementHorizonView> Retirements)
{
    /// <summary>What RealEstate projected, with the outcomes of the scenario's sales and repayments (projection spec §5.2).</summary>
    public RealEstateProjection RealEstate { get; init; } = new([], []);

    /// <summary>
    /// Inflation spec D5: the last 12 months' observed inflation when it is the rate of the current year's months on the grid; null
    /// when the year has its own rate, the reference is not EUR, no series is stored, or no month of the year is ahead.
    /// </summary>
    public decimal? ObservedInflation { get; init; }
}

/// <summary>
/// Projection spec §3.2 and §6: the reader's figures in a scope, converted to the reference at today's rate, as the dashboard and the
/// purchase scenario read them. A currency without a rate is left out and named.
/// </summary>
public sealed class ProjectionFactsReader(
    ScenarioFactsReader scenario, IEnumerable<INetWorthContribution> netWorth, IEnumerable<IIncomeContribution> income,
    IRealEstateProjection realEstate, IPersonRegistry persons, IActorAccessor actor, IHouseholdSettings household, IFxRates fx,
    IInflation inflation)
{
    public const string AutoSavings = "auto";
    public const string HouseholdSavings = "household";

    /// <summary>Spec §2.3: no retirement past 40 years.</summary>
    private const int MaxMonths = ProjectionHorizon.MaxYears * 12;

    private PersonId Reader => actor.Current!.PersonId!.Value;

    public Task<Result<ProjectionReading>> ReadAsync(VisibilityScope scope, ProjectionHorizon horizon, CancellationToken ct)
        => ReadAsync(scope, horizon, null, RealEstateEvents.None, ct);

    /// <summary>
    /// Projection spec §4 and §6: a scenario's reading, on <paramref name="assumptions"/> (null: the household's) and with RealEstate
    /// applying <paramref name="events"/>; the base is <see cref="RealEstateEvents.None"/> on the household's assumptions.
    /// </summary>
    public async Task<Result<ProjectionReading>> ReadAsync(
        VisibilityScope scope, ProjectionHorizon horizon, ProjectionAssumptions? assumptions, RealEstateEvents events, CancellationToken ct)
    {
        var read = await scenario.TodayAsync(scope, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<ProjectionReading>(failure!);
        read.TryGetValue(out var now);
        var today = now!.Date;
        var reference = now.Reference;

        var span = await MonthsAsync(horizon, today, ct);
        if (span.TryGetFailure(out failure)) return Result.Failure<ProjectionReading>(failure!);
        span.TryGetValue(out var months);
        var thisMonth = today.ToYearMonth();
        var horizonOn = thisMonth.PlusMonths(months).ToDateInterval().End;

        assumptions = ProjectionRules.WithoutPastYears(assumptions ?? await household.ProjectionAssumptionsAsync(ct), today); // C15

        // Spec §2.1: the automatic savings are the last 12 months' net contributions, both ends included.
        var saved = assumptions.MonthlySavings is null
            ? await Contributions.CollectAsync(netWorth, c => c.NetContributionsAsync(Reader, scope, today.PlusYears(-1).PlusDays(1), today, ct))
            : Result.Success<IReadOnlyList<IReadOnlyDictionary<Currency, decimal>>>([]);
        if (saved.TryGetFailure(out failure)) return Result.Failure<ProjectionReading>(failure!);
        saved.TryGetValue(out var contributions);

        var people = await persons.ListAsync(ct);
        var switches = Departures.SwitchMonths(people, today);
        var timelines = await Contributions.CollectAsync(income, c => c.TimelineAsync(Reader, scope, today, months, switches, ct));
        if (timelines.TryGetFailure(out failure)) return Result.Failure<ProjectionReading>(failure!);
        timelines.TryGetValue(out var earned);

        var projected = await realEstate.ProjectAsync(Reader, scope, today, months,
            assumptions.ClassRates.GetValueOrDefault(AllocationClasses.RealEstate), events, ct);
        if (projected.TryGetFailure(out failure)) return Result.Failure<ProjectionReading>(failure!);
        projected.TryGetValue(out var property);

        var foreign = contributions!.SelectMany(c => c.Keys).Concat(earned!.SelectMany(t => t.Monthly.SelectMany(m => m.Keys)))
            .Where(c => c != reference).Distinct().ToList();
        // Rates are per USD: the reference's own quote is needed too.
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. foreign, reference], today, today, ct);
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        decimal Converted(IEnumerable<IReadOnlyDictionary<Currency, decimal>> parts)
        {
            var total = 0m;
            foreach (var (currency, amount) in DashboardMath.Merge(parts))
                if (amount == 0m) continue;
                else if (rates.TryConvert(amount, currency, reference, today, projected: false, out var converted, out _)) total += converted;
                else missing.Add(currency.Code);
            return total;
        }

        // Spec §3.2: the savings as the purchase scenario reads them; a holding with a value but no rate is named.
        foreach (var holding in now.Savings)
            if (holding.Converted is null && holding.Holding.Value is { Amount: not 0m } value)
                missing.Add(value.Currency.Code);
        // Retirement spec §2: a PER's locked slices open at its holder's departure (the latest of its owners).
        var adultIds = people.Where(p => p.Kind == PersonKind.Adult).Select(p => p.Id).ToHashSet();
        var slices = now.Savings.Where(s => s.Converted is not null).SelectMany(s =>
            s.Holding.UnlocksAtRetirement && Departures.SwitchOf(s.Holding.Owners.Select(o => o.PersonId).Where(adultIds.Contains), switches) is { } unlock
                ? s.Slices.Select(x => x.Liquidity == AllocationLiquidity.Locked ? x with { UnlocksAt = Math.Max(0, unlock) } : x)
                : s.Slices).ToList();

        var monthlySavings = assumptions.MonthlySavings ?? Converted(contributions!) / 12m;
        // Inflation spec C7, D5: the current year falls back on the observed inflation, for a EUR household only.
        var observed = reference == Currency.Eur ? (await inflation.ReadAsync(ct)).Last12Months?.Rate : null;
        var resolved = ProjectionRates.Resolve(assumptions, observed, today, months, slices.Select(s => s.Class).Append(assumptions.SavingsClass));
        var growth = resolved.Income;
        // C17, D8: each part is indexed from the month its amount is in; a timeline without parts (rents) is in today's money.
        var incomes = Enumerable.Range(0, months + 1).Select(k => earned!
            .SelectMany(t => t.Parts?[k] ?? t.Monthly[k].Select(m => new TimelinePart(m.Key, m.Value, 0)))
            .GroupBy(p => p.IndexedFrom)
            .Sum(from => Converted(from.Select(p => new Dictionary<Currency, decimal> { [p.Currency] = p.Amount })) * growth[k] / growth[from.Key]))
            .ToList();

        var milestones = property!.LoanEnds.Select(e => new ProjectionMilestone(e.On, "loan_end", e.Name, e.Loan))
            .Concat(property.PropertiesFree.Select(p => new ProjectionMilestone(p.On, "property_free", p.Name, p.Property)))
            .ToList();
        // Retirement spec §1: every adult's departure is a milestone (the engine keeps those inside the grid).
        milestones.AddRange(people.Where(p => p.Kind == PersonKind.Adult)
            .Select(p => (Person: p, Month: Departures.MonthOf(p)))
            .Where(d => d.Month is not null)
            .Select(d => new ProjectionMilestone(d.Month!.Value.ToDateInterval().End, "retirement", d.Person.DisplayName, null)));

        // Spec §2.3: the retirements offered as a horizon are the adults' the route accepts.
        var retirements = people
            .Where(p => p.Kind == PersonKind.Adult)
            .Select(p => (Person: p, Months: RetirementMonths(p, today)))
            .Where(r => r.Months is not null)
            .Select(r => new RetirementHorizonView(r.Person.Id.Value, r.Person.DisplayName, thisMonth.PlusMonths(r.Months!.Value).ToDateInterval().End))
            .OrderBy(r => r.On).ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        var inputs = new ProjectionInputs(today, months, slices, incomes, property.Months, monthlySavings, assumptions, milestones, Rates: resolved);
        return Result.Success(new ProjectionReading(
            inputs, reference, horizonOn, assumptions.MonthlySavings is null ? AutoSavings : HouseholdSavings,
            earned!.SelectMany(t => t.Extended).DistinctBy(e => e.Source).ToList(),
            missing.Union(property.MissingCurrencies).Order(StringComparer.Ordinal).ToList(), retirements)
        {
            RealEstate = property,
            ObservedInflation = ProjectionRules.ShowsObserved(assumptions, today) ? observed : null,
        });
    }

    /// <summary>
    /// Spec §2.2 and §2.3: the months from this one to the person's departure month (<see cref="Departures.MonthOf"/>);
    /// <c>null</c> without a birth date, or when it has no month ahead or lies past 40 years.
    /// </summary>
    private static int? RetirementMonths(PersonInfo person, LocalDate today)
    {
        if (Departures.MonthOf(person) is not { } retires) return null;
        var months = (retires.Year - today.Year) * 12 + retires.Month - today.Month;
        return months is <= 0 or > MaxMonths ? null : months;
    }

    /// <summary>
    /// Spec §2.2 and §2.3: the months from this one to the horizon's; a retirement is refused when <see cref="RetirementMonths"/>
    /// has none.
    /// </summary>
    internal async Task<Result<int>> MonthsAsync(ProjectionHorizon horizon, LocalDate today, CancellationToken ct)
    {
        if (horizon.RetireOf is not { } id) return Result.Success(horizon.Years!.Value * 12);
        var person = await persons.FindAsync(id, ct);
        if (person?.BirthDate is null)
            return Result.Failure<int>(new Failure(ProjectionCodes.NoBirthDate, "This person has no birth date.", null));
        return RetirementMonths(person, today) is { } months
            ? Result.Success(months)
            : Result.Failure<int>(new Failure(ProjectionCodes.InvalidHorizon,
                "This retirement is past or more than 40 years away.", null));
    }
}
