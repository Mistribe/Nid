using Nid.Core.Application.Dashboard;
using Nid.Core.Application.Tax;
using Nid.Core.Contracts;
using Nid.Core.Domain.Scenarios;
using Nid.Core.Domain.Tax;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Application.Scenarios;

public static class ScenarioCodes
{
    public const string InvalidField = "scenario.invalid_field";
    public const string FundingNotFound = "scenario.funding_not_found";
    public const string FundingLocked = "scenario.funding_locked";
}

/// <summary>A holding of the savings (any module but <c>properties</c> and <c>loans</c>) at today's rate; <c>Converted</c> null without a value or a rate.</summary>
public sealed record SavingsHolding(Holding Holding, decimal? Converted, IReadOnlyList<SavingsSlice> Slices, string Liquidity);

/// <summary>The reader's figures of today in a scope, converted to the reference; <c>Missing</c> names the currencies left out.</summary>
public sealed record ScenarioToday(
    LocalDate Date, Currency Reference, IReadOnlyList<SavingsHolding> Savings, decimal? MonthlyIncome,
    decimal Subscriptions, decimal PropertyCosts, decimal Commitments, IReadOnlyList<string> Missing);

/// <summary>
/// What the calculator reads, for the reader: <c>Document</c> is the scenario without the funding lines the reader cannot see
/// (<c>HiddenFunding</c> of them, spec §5); <c>TaxUnavailable</c> when a rental's tax could not be estimated.
/// </summary>
public sealed record ScenarioReading(
    ScenarioFacts Facts, int HiddenFunding, IReadOnlyList<string> Missing, PropertyPurchase Document, Currency Currency, bool TaxUnavailable);

/// <summary>Property purchase scenario spec §4 and §5: gathers the household's figures as the dashboard and the tax estimate read them.</summary>
public sealed class ScenarioFactsReader(
    IEnumerable<INetWorthContribution> netWorth, IEnumerable<IIncomeContribution> income, IEnumerable<ICommitmentsContribution> commitments,
    IRealEstateProjection realEstate, ILoanSimulator loans, TaxEstimates tax, IActorAccessor actor, IHouseholdCalendar calendar,
    IHouseholdSettings household, IFxRates fx)
{
    private PersonId Reader => actor.Current!.PersonId!.Value;

    public async Task<Result<ScenarioToday>> TodayAsync(VisibilityScope scope, CancellationToken ct)
    {
        var today = await calendar.TodayAsync(ct);
        var worth = await Contributions.CollectAsync(netWorth, c => c.NetWorthAsync(Reader, scope, today, ct));
        if (worth.TryGetFailure(out var failure)) return Result.Failure<ScenarioToday>(failure!);
        var earned = await Contributions.CollectAsync(income, c => c.IncomeAsync(Reader, scope, today, ct));
        if (earned.TryGetFailure(out failure)) return Result.Failure<ScenarioToday>(failure!);
        var committed = await Contributions.CollectAsync(commitments, c => c.CommitmentsAsync(Reader, scope, today, ct));
        if (committed.TryGetFailure(out failure)) return Result.Failure<ScenarioToday>(failure!);
        worth.TryGetValue(out var w);
        earned.TryGetValue(out var e);
        committed.TryGetValue(out var c);

        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = DashboardMath.Currencies(w!, c!, e!).Where(x => x != reference).ToList();
        // Rates are per USD: the reference's own quote is needed too, as the dashboard loads them.
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync(foreign.Append(reference).ToList(), today, today, ct);
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        decimal Sum(IEnumerable<IReadOnlyDictionary<Currency, decimal>> parts)
        {
            var total = 0m;
            foreach (var (currency, amount) in DashboardMath.Merge(parts))
                if (amount != 0m && rates.TryConvert(amount, currency, reference, today, projected: false, out var converted, out _))
                    total += converted;
                else if (amount != 0m)
                    missing.Add(currency.Code);
            return total;
        }

        // Review Focus 1: properties and loans come from IRealEstateProjection, never also from their dashboard holding.
        var savings = w!.SelectMany(p => p.Holdings).Where(h => h.Module is not ("properties" or "loans")).Select(h =>
        {
            var (amount, miss) = DashboardMath.Convert(h.Value, reference, rates, today);
            if (miss is not null) missing.Add(miss);
            var slices = DashboardMath.SlicesOf(h, h.Value?.Amount ?? 0m);
            var liquidity = slices.Select(s => s.Liquidity).MaxBy(Rank) ?? AllocationLiquidity.Available;
            IReadOnlyList<SavingsSlice> parts = h.Value is { } v && v.Amount != 0m && amount is { } a
                ? slices.Select(s => new SavingsSlice(h.Item, s.Class, s.Liquidity, s.Amount * a / v.Amount)).ToList()
                : [];
            return new SavingsHolding(h, amount, parts, liquidity);
        }).ToList();

        decimal Cash(string source) => Sum(c!.Where(p => p.Source == source).Select(p => p.Cash));
        return Result.Success(new ScenarioToday(
            today, reference, savings, e!.Sum(p => p.Sources) > 0 ? Sum(e!.Select(p => p.MonthlyEquivalent)) : null,
            Cash(CommitmentSources.Subscriptions), Cash(CommitmentSources.PropertyCosts), Sum(c!.Select(p => p.Cash)), missing.ToList()));
    }

    public async Task<Result<ScenarioReading>> ReadAsync(PropertyPurchase doc, VisibilityScope scope, CancellationToken ct)
    {
        var read = await TodayAsync(scope, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<ScenarioReading>(failure!);
        read.TryGetValue(out var today);

        // Spec §5: a funding line the reader cannot see is left out of the calculation and of the document they get back.
        var visible = today!.Savings.Select(s => s.Holding.Item).ToHashSet();
        var shown = doc with { Funding = doc.Funding.Where(f => visible.Contains(f.Item)).ToList() };

        // Review Focus 3: a purchase dated in the past is bought today, so no payment of its loans lies in the past.
        var simulated = Simulate(today.Reference, Max(doc.PurchaseOn, today.Date), doc.Loans);
        if (simulated.TryGetFailure(out failure)) return Result.Failure<ScenarioReading>(failure!);
        simulated.TryGetValue(out var scenarioLoans);

        // The existing loans are read up to the scenario's last payment (the retained month can lie past the horizon).
        var lastDue = scenarioLoans!.SelectMany(l => l.Rows).Select(r => r.DueDate).DefaultIfEmpty(today.Date).Max();
        var months = Math.Max(doc.Assumptions.HorizonYears * 12, (lastDue.Year - today.Date.Year) * 12 + lastDue.Month - today.Date.Month + 1);
        var projected = await realEstate.ProjectAsync(Reader, scope, today.Date, months,
            doc.Assumptions.ClassRates.GetValueOrDefault(AllocationClasses.RealEstate), ct);
        if (projected.TryGetFailure(out failure)) return Result.Failure<ScenarioReading>(failure!);
        projected.TryGetValue(out var existing);

        var (increase, unavailable) = await TaxIncreaseAsync(shown, scope, today.Date, scenarioLoans!, ct);
        var facts = new ScenarioFacts(
            today.Date, today.MonthlyIncome, today.Subscriptions, today.PropertyCosts, today.Savings.SelectMany(s => s.Slices).ToList(),
            existing!.Months, scenarioLoans!, increase);
        return Result.Success(new ScenarioReading(
            facts, doc.Funding.Count - shown.Funding.Count, today.Missing.Union(existing.MissingCurrencies).Order(StringComparer.Ordinal).ToList(),
            shown, today.Reference, unavailable));
    }

    /// <summary>
    /// Spec §3 and §6, on a write: the document's limits, its loans as the engine reads them, and each funding line;
    /// <c>Visible</c> is the writer's savings holdings in the scope.
    /// </summary>
    public async Task<(Failure? Refused, IReadOnlySet<ItemId> Visible)> RefusalAsync(PropertyPurchase doc, VisibilityScope scope, CancellationToken ct)
    {
        var read = await TodayAsync(scope, ct);
        if (read.TryGetFailure(out var failure)) return (failure, new HashSet<ItemId>());
        read.TryGetValue(out var today);
        return (Refusal(doc, today!), today!.Savings.Select(s => s.Holding.Item).ToHashSet());
    }

    private Failure? Refusal(PropertyPurchase doc, ScenarioToday today)
    {
        if (Check(doc, today.Date) is { } invalid) return invalid;
        if (Simulate(today.Reference, Max(doc.PurchaseOn, today.Date), doc.Loans).TryGetFailure(out var failure)) return failure;

        for (var i = 0; i < doc.Funding.Count; i++)
        {
            var line = doc.Funding[i];
            var holding = today.Savings.FirstOrDefault(s => s.Holding.Item == line.Item);
            if (holding is null)
                return FieldErrors.At(new Failure(ScenarioCodes.FundingNotFound, "No such envelope.", null), $"funding[{i}].item");
            if (holding.Liquidity == AllocationLiquidity.Locked)
                return FieldErrors.At(new Failure(ScenarioCodes.FundingLocked, "A locked envelope cannot fund the purchase.", null), $"funding[{i}].item");
            if (line.Amount > (holding.Converted ?? 0m))
                return FieldErrors.At(new Failure(ScenarioCodes.InvalidField, "Above the envelope's value.", null), $"funding[{i}].amount");
        }
        return null;
    }

    /// <summary>Every refusal of <see cref="ScenarioRules.Check"/>, located; null when the document is valid.</summary>
    public static Failure? Check(PropertyPurchase doc, LocalDate today)
    {
        var errors = ScenarioRules.Check(doc, today);
        return errors.Count == 0
            ? null
            : FieldErrors.Many(ScenarioCodes.InvalidField, errors.Count == 1 ? errors[0].Message : "Some fields are not valid.",
                errors.Select(e => new FieldError(e.Field, ScenarioCodes.InvalidField, e.Message)).ToList());
    }

    /// <summary>The engine's refusal names its loan in <c>field</c>: located as every other refusal.</summary>
    private Result<IReadOnlyList<SimulatedLoan>> Simulate(Currency currency, LocalDate on, IReadOnlyList<ScenarioLoan> scenarioLoans)
    {
        var simulated = loans.Simulate(currency, on, scenarioLoans);
        return simulated.TryGetFailure(out var failure) && failure!.Metadata is { } metadata && metadata.TryGetValue("field", out var at) && at is string field
            ? Result.Failure<IReadOnlyList<SimulatedLoan>>(FieldErrors.At(new Failure(failure.Code, failure.Message, null), field))
            : simulated;
    }

    /// <summary>
    /// Spec §4.1: the engine replayed on this year's inputs plus the property's first full year (rents, and under <c>real</c> the
    /// charges and the first 12 rows' interest and insurance); the monthly difference of total tax. A primary home raises nothing.
    /// </summary>
    private async Task<(decimal Increase, bool Unavailable)> TaxIncreaseAsync(
        PropertyPurchase doc, VisibilityScope scope, LocalDate today, IReadOnlyList<SimulatedLoan> scenarioLoans, CancellationToken ct)
    {
        if (doc.Use != PurchaseUse.Rental) return (0m, false);
        var read = await tax.InputsAsync(today.Year, scope, today, ct);
        if (!read.TryGetValue(out var inputs)) return (0m, true);
        var regime = TaxEstimates.TryRegime(doc.RentalRegime, out var chosen) ? chosen : inputs.RentalRegime;
        var real = regime == RentalRegime.Real;
        var replayed = inputs with
        {
            RentalGross = inputs.RentalGross + doc.ExpectedRent * (12 - doc.VacancyMonths),
            RentalCharges = inputs.RentalCharges + (real ? doc.MonthlyCharges * 12 + 20m : 0m),   // + the engine's flat charge per rental (TaxUseCases)
            RentalInterest = inputs.RentalInterest + (real ? scenarioLoans.Sum(l => l.Rows.Take(12).Sum(r => r.Interest + r.Insurance)) : 0m),
            RentalRegime = regime,
        };
        return ((TaxCalculator.Compute(replayed).TotalTax - TaxCalculator.Compute(inputs).TotalTax) / 12m, false);
    }

    private static int Rank(string liquidity) => liquidity switch
    {
        AllocationLiquidity.Locked => 2,
        AllocationLiquidity.Days => 1,
        _ => 0,
    };

    private static LocalDate Max(LocalDate a, LocalDate b) => a > b ? a : b;
}
