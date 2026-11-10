using Nid.Core.Application.Scenarios;
using Nid.Core.Contracts;
using Nid.Core.Domain.Projection;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Projection;

/// <summary>Retirement spec §4: the retirement page's single read, the base projection at 40 years in the reader's scope. Writes nothing.</summary>
public sealed record GetRetirementQuery(VisibilityScope Scope) : IQuery<RetirementView>;

/// <summary>A pension source the reader can see: its monthly amount in its own currency (null when none is running or ahead).</summary>
public sealed record PensionSourceView(ItemId Source, string Name, string? Monthly, string? Currency);

/// <summary>
/// An adult's departure: the age asked, the month (<c>yyyy-MM</c>, null without a birth date) and their pension at the switch month in
/// today's money (null when the switch is off the grid or they have no pension in view).
/// </summary>
public sealed record DepartureView(
    Guid Person, string Name, string? BirthDate, int Years, int Months, string? Departure, string? Pension, IReadOnlyList<PensionSourceView> Sources);

/// <summary>Retirement spec §3: the figures at the retirement month <c>Month</c> (<c>yyyy-MM</c>), in today's money rounded to the cent.</summary>
public sealed record RetirementFiguresView(string Month, string IncomeNow, string IncomeAtRetirement, string Gap, string CapitalNeeded, string CapitalAtRetirement);

/// <summary>Retirement spec §3: a month's capital, what it covers and the expenses to cover, in today's money.</summary>
public sealed record IndependencePointView(LocalDate On, string Capital, string Covered, string Expenses);

/// <summary>
/// Retirement spec §4. <c>Warnings</c> are the projection's plus <c>living_cost_missing</c> when the living cost is 0, the only place
/// that warning is raised.
/// </summary>
public sealed record RetirementView(
    string Currency, LocalDate HorizonOn, IReadOnlyList<DepartureView> Departures, RetirementFiguresView? Figures, string? FinancialIndependence,
    IReadOnlyList<IndependencePointView> Independence, ProjectionAssumptionsView Assumptions, IReadOnlyList<ProjectionWarningView> Warnings)
    : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Departures.SelectMany(d => d.Sources.Select(s => s.Source));
}

[RequestHandler<GetRetirementQuery, RetirementView>]
public sealed class GetRetirementHandler(ProjectionFactsReader facts, IPersonRegistry persons, IActorAccessor actor)
    : IRequestHandler<GetRetirementQuery, RetirementView>
{
    private static readonly ProjectionHorizon Horizon = new(ProjectionHorizon.MaxYears, null);

    public async ValueTask<Result<RetirementView>> HandleAsync(GetRetirementQuery query, CancellationToken ct = default)
    {
        var read = await facts.ReadAsync(query.Scope, Horizon, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<RetirementView>(failure!);
        read.TryGetValue(out var reading);
        var inputs = reading!.Inputs;
        var result = HouseholdProjection.Project(inputs);
        var view = ProjectionView.From(Horizon, reading, result);
        static string Amount(decimal v) => PropertyPurchaseView.Amount(v);

        var adults = (await persons.ListAsync(ct)).Where(p => p.Kind == PersonKind.Adult).ToList();
        var switches = Departures.SwitchMonths(adults, inputs.Today);
        var inflation = inputs.Rates!.Inflation;
        bool OnGrid(int k) => k >= 1 && k <= inputs.Months;

        var departures = adults.OrderBy(p => p.DisplayName, StringComparer.Ordinal).Select(p =>
        {
            string? pension = null;
            if (switches.TryGetValue(p.Id, out var k) && OnGrid(k) && reading.PensionsByHolder.TryGetValue(p.Id, out var series))
                pension = Amount(series[k] / inflation[k]);
            var sources = reading.PensionSources.Where(s => s.Holder == p.Id)
                .Select(s => new PensionSourceView(s.Source, s.Name, s.Monthly is { } m ? Amount(m.Amount) : null, s.Monthly?.Currency.Code)).ToList();
            return new DepartureView(p.Id.Value, p.DisplayName, p.BirthDate?.ToString("yyyy-MM-dd", null), p.RetirementAge, p.RetirementMonths,
                Departures.MonthOf(p) is { } month ? $"{month.Year:0000}-{month.Month:00}" : null, pension, sources);
        }).ToList();

        // Retirement spec §3: the last adult to leave inside the grid, or the reader's own.
        var inGrid = switches.Where(s => query.Scope == VisibilityScope.Me ? s.Key == actor.Current!.PersonId : true)
            .Select(s => s.Value).Where(OnGrid).ToList();
        int? kR = inGrid.Count == 0 ? null : inGrid.Max();
        var figures = RetirementFigures.Of(inputs.Income, inflation, result.Independence, kR, inputs.Assumptions.WithdrawalRate);

        var warnings = view.Warnings.ToList();
        if (inputs.Assumptions.MonthlyLivingCost == 0m) warnings.Add(new ProjectionWarningView("living_cost_missing", null));
        return Result.Success(new RetirementView(
            view.Currency, view.HorizonOn, departures,
            figures is null ? null : new RetirementFiguresView(
                inputs.Today.ToYearMonth().PlusMonths(figures.Month).ToString("yyyy-MM", null),
                Amount(figures.IncomeNow), Amount(figures.IncomeAtRetirement), Amount(figures.Gap), Amount(figures.CapitalNeeded), Amount(figures.CapitalAtRetirement)),
            view.FinancialIndependence,
            result.Independence.Select(p => new IndependencePointView(p.On, Amount(p.CapitalReal), Amount(p.CoveredReal), Amount(p.ExpensesReal))).ToList(),
            view.Assumptions, warnings));
    }
}
