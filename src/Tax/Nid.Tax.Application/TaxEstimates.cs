using System.Globalization;
using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using Nid.Tax.Domain;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Tax.Application;

/// <summary>Issue #12: a year's computed estimate; <c>HasData</c>: the year has a tax line or a profile stored for that very year.</summary>
public sealed record TaxComputation(TaxResult Result, bool HasData);

/// <summary>Spec §6 and §7: reads every module's lines, applies the profile, computes.</summary>
public sealed partial class TaxEstimates(
    IEnumerable<ITaxContribution> contributions, ITaxProfileStore store, ITaxAssessmentStore assessments, IPersonRegistry persons, IActorAccessor actor)
{
    public async Task<Result<TaxEstimateView>> EstimateAsync(int year, VisibilityScope scope, NodaTime.LocalDate today, CancellationToken ct)
    {
        var read = await ReadAsync(year, scope, today, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<TaxEstimateView>(failure!);
        read.TryGetValue(out var r);
        var (lines, stored, people, previous) = (r!.Lines, r.Stored, r.People, r.PreviousYearIncome);
        var (inputs, notices, columns) = Assemble(year, lines, r.Effective, people, previous, r.ReferenceBefore);
        var adults = Adults(people);
        var result = TaxCalculator.Compute(inputs);
        // The history below asks for this very year: hand it the figure instead of reading every module again.
        computed[(actor.Current!.PersonId!.Value, year, scope, today)] = new TaxComputation(result, lines.Count > 0 || stored?.Year == year);
        var declaration = Declaration(year, lines, inputs, result, columns, notices);
        // The deficit last year's estimate leaves, offered only where no notice of last year prints its own and a property is rented.
        string? deficitEstimated = null;
        if (year - 1 >= TaxRules.FirstYear && r.Carry?.RentalDeficitCarried is null && lines.Any(l => l.Code == TaxLineCodes.RentalProperty))
        {
            var last = await ComputeAsync(year - 1, scope, today, ct);
            if (last.TryGetFailure(out failure)) return Result.Failure<TaxEstimateView>(failure!);
            if (last.TryGetValue(out var before) && before.RentalDeficitCarriedForward > 0m)
                deficitEstimated = Amount(before.RentalDeficitCarriedForward);
        }
        // Tax-per spec §4.5: the PERs each declarant's contributions were read from.
        IReadOnlyList<IReadOnlyList<string>> perEnvelopes = adults
            .Select(a => (IReadOnlyList<string>)lines.Where(l => l.Code == TaxLineCodes.PerContribution && l.Person == a.Id)
                .Select(l => l.Name).Distinct().Order(StringComparer.Ordinal).ToList()).ToList();
        var historyRead = await HistoryAsync(year, scope, today, ct);
        if (historyRead.TryGetFailure(out failure)) return Result.Failure<TaxEstimateView>(failure!);
        historyRead.TryGetValue(out var history);
        var thisYear = result.IncomeTax + result.CapitalFlatTax;
        var lastYear = history!.FirstOrDefault(y => y.Year == year - 1);
        TaxPreviousView? previousYear = null;
        if (lastYear is not null)
        {
            var was = decimal.Parse(lastYear.Tax, CultureInfo.InvariantCulture);
            previousYear = new TaxPreviousView(lastYear.Year, lastYear.Source, lastYear.Tax, Amount(Math.Round(thisYear, 2, MidpointRounding.AwayFromZero) - was),
                was == 0m ? null : Math.Round((Math.Round(thisYear, 2, MidpointRounding.AwayFromZero) - was) / Math.Abs(was) * 100m, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture));
        }
        return Result.Success(new TaxEstimateView(
            year, TaxProfileView.Of(stored?.Profile ?? TaxProfile.Empty), stored is { } s && s.Year != year ? s.Year : null,
            TaxInputsView.Of(inputs, perEnvelopes), TaxResultView.Of(result, adults.Select(a => a.Id.Value).ToList()), notices,
            adults.Select(a => new TaxAdultView(a.Id.Value, a.DisplayName)).ToList(),
            people.Count(p => p.Kind == PersonKind.Dependent),
            lines.Where(l => l.Code == TaxLineCodes.RentalProperty).Select(l => new TaxPropertyView(l.Item, l.Name, l.On!.Value.Year)).ToList(),
            lines.Where(l => l.Code == TaxLineCodes.SalaryDetail && l.Person is { } p && adults.Any(a => a.Id == p))
                .OrderBy(l => l.Person!.Value.Value).ThenBy(l => l.On).ThenBy(l => l.Name, StringComparer.Ordinal)
                .Select(l => new TaxSalaryDetailView(l.Person!.Value.Value, l.Item, l.Name, l.On!.Value, l.Note, l.Category!, Amount(l.Amount))).ToList(),
            adults.Select(a => (a.Id.Value, Rate: ObservedRate.Of(lines, a.Id))).Where(x => x.Rate is not null)
                .ToDictionary(x => x.Value, x => Percent(x.Rate!.Value)),
            r.Carry is { } carry ? TaxCarryView.Of(carry) : null, GapsOf(r.YearNotices, result, adults.Select(a => a.Id.Value).ToList()), deficitEstimated,
            declaration, previousYear, history, stored is { } own && own.Year == year ? own.Version : null));
    }

    /// <summary>
    /// Issue #307: each year from the earliest notice (or the first year of the rules) to <paramref name="upTo"/>: the notice's net tax when
    /// the reader has one (as <see cref="GetTaxChangesHandler"/> reads them), else the year's estimate, else the year is left out.
    /// </summary>
    public async Task<Result<IReadOnlyList<TaxYearView>>> HistoryAsync(int upTo, VisibilityScope scope, NodaTime.LocalDate today, CancellationToken ct)
    {
        var people = await persons.ListAsync(ct);
        var notices = (await assessments.ListForAsync(actor.Current!.PersonId!.Value, null, ct))
            .Where(n => n.Assessment.NetTax is not null && n.Assessment.Year <= upTo && (scope != VisibilityScope.Household || TaxAssessmentAccess.InHousehold(n.Assessment, people)))
            .Select(n => n.Assessment).ToList();
        var years = new List<TaxYearView>();
        for (var year = Math.Min(TaxRules.FirstYear, notices.Select(n => n.Year).DefaultIfEmpty(upTo).Min()); year <= upTo; year++)
        {
            var own = notices.Where(n => n.Year == year).ToList();
            if (own.Count > 0)
            {
                // Two separate notices of a year add up (as the reference incomes do); the rate is only meaningful for one.
                var single = own.Count == 1 ? own[0] : null;
                years.Add(new TaxYearView(year, "assessment", Amount(own.Sum(n => n.NetTax!.Value)),
                    own.All(n => n.TaxableIncome is not null) ? Amount(own.Sum(n => n.TaxableIncome!.Value)) : null,
                    single?.AverageRate is { } rate ? Percent(rate / 100m) : null));
            }
            else if (year >= TaxRules.FirstYear)
            {
                var computed = await ComputeWithDataAsync(year, scope, today, ct);
                if (computed.TryGetFailure(out var failure))
                {
                    // A failing earlier year is no point on the curve; the year shown still fails the request.
                    if (year != upTo) continue;
                    return Result.Failure<IReadOnlyList<TaxYearView>>(failure!);
                }
                computed.TryGetValue(out var data);
                // The year shown (upTo) always stands; an earlier year with nothing to estimate from is no point on the curve.
                if (!data!.HasData && year != upTo) continue;
                var r = data.Result;
                years.Add(new TaxYearView(year, "estimate", Amount(r!.IncomeTax + r.CapitalFlatTax), Amount(r.NetTaxableIncome), r.AverageRate is { } a ? Percent(a) : null));
            }
        }
        return Result.Success<IReadOnlyList<TaxYearView>>(years);
    }

    /// <summary>The notices of what the estimate leaves out, so of the boxes the household fills itself.</summary>
    private static readonly HashSet<string> NotFilled = ["unsupported_income", "withdrawal_not_estimated", TaxLineCodes.OtherCurrency, "not_a_declarant"];

    /// <summary>
    /// Issue #233: the 2042 boxes from the estimate's inputs and result, the 2044's totals for the rental ones; the childcare costs by the
    /// box their statements name (the profile's amount, without statements, in 7GA).
    /// </summary>
    private static TaxDeclarationView Declaration(
        int year, IReadOnlyList<TaxLine> lines, TaxInputs inputs, TaxResult result, IReadOnlyList<Rental2044Column> columns, IReadOnlyList<TaxNoticeView> notices)
    {
        var childcare = lines.Where(l => l.Code is TaxLineCodes.ChildcareOutsideHome or TaxLineCodes.ChildcareProjected && l.Category is not null)
            .Select(l => (l.Category!, l.Amount)).ToList();
        if (!lines.Any(l => l.Code is TaxLineCodes.ChildcareOutsideHome or TaxLineCodes.ChildcareProjected))
            childcare.Add(("7GA", inputs.ChildcareOutsideHome));
        var rental = Rental2044.Totals(columns, TaxRules.For(year).DeficitCap(inputs.RentalEnergyWorks, inputs.RentalCharges));
        return new TaxDeclarationView(
            Tax2042.Boxes(inputs, result, rental, childcare).Select(b => new Tax2042BoxView(b.Code, b.Person, Amount(b.Amount))).ToList(),
            notices.Where(n => NotFilled.Contains(n.Code)).ToList());
    }

    /// <summary>Tax assessments spec §6: the gaps of the year's notice whose declarants are the estimate's adults, if there is one.</summary>
    private static TaxGapsView? GapsOf(IEnumerable<StoredTaxAssessment> yearNotices, TaxResult result, IReadOnlyList<Guid> adults)
        => yearNotices.Select(n => (n.Id, Gaps: TaxGaps.Of(n.Assessment, result, adults))).Where(x => x.Gaps is not null)
            .Select(x => TaxGapsView.Of(x.Id, x.Gaps!)).FirstOrDefault();

    /// <summary>The year's inputs as the estimate computes them (the property purchase scenario replays the engine on them).</summary>
    public async Task<Result<TaxInputs>> InputsAsync(int year, VisibilityScope scope, NodaTime.LocalDate today, CancellationToken ct)
    {
        var read = await ReadAsync(year, scope, today, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<TaxInputs>(failure!);
        read.TryGetValue(out var r);
        return Result.Success(Assemble(year, r!.Lines, r.Effective, r.People, r.PreviousYearIncome, r.ReferenceBefore).Inputs);
    }

    /// <summary>
    /// The year's result alone, as <see cref="EstimateAsync"/> computes it. Kept for the rest of the scope (a request): the dashboard asks
    /// twice, for « Impôts » and the PER deadline. Keyed by the reader too, as a job's scope may read for several.
    /// </summary>
    public async Task<Result<TaxResult>> ComputeAsync(int year, VisibilityScope scope, NodaTime.LocalDate today, CancellationToken ct)
        => (await ComputeWithDataAsync(year, scope, today, ct)).Map(c => c.Result);

    /// <summary>
    /// <see cref="ComputeAsync"/>'s result, from the same cache, and whether the year has anything to estimate from: a tax line, or
    /// a profile stored for that very year (an earlier one standing in alone is not).
    /// </summary>
    public async Task<Result<TaxComputation>> ComputeWithDataAsync(int year, VisibilityScope scope, NodaTime.LocalDate today, CancellationToken ct)
    {
        var key = (actor.Current!.PersonId!.Value, year, scope, today);
        if (computed.TryGetValue(key, out var kept)) return Result.Success(kept);
        var read = await ReadAsync(year, scope, today, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<TaxComputation>(failure!);
        read.TryGetValue(out var r);
        var (inputs, _, _) = Assemble(year, r!.Lines, r.Effective, r.People, r.PreviousYearIncome, r.ReferenceBefore);
        return Result.Success(computed[key] = new TaxComputation(TaxCalculator.Compute(inputs), r.Lines.Count > 0 || r.Stored?.Year == year));
    }

    private readonly Dictionary<(PersonId Reader, int Year, VisibilityScope Scope, NodaTime.LocalDate Today), TaxComputation> computed = [];

    /// <summary>
    /// Rental 2044 spec §4–§5: the form from the estimate's own lines and calculation, so the two never differ; the regime is the
    /// estimate's, the totals <see cref="Rental2044.Totals"/>' and the 2042 boxes theirs (4BE alone under the micro-foncier).
    /// </summary>
    public async Task<Result<Rental2044View>> Rental2044Async(int year, VisibilityScope scope, NodaTime.LocalDate today, CancellationToken ct)
    {
        // The form only needs the rental regime, which the PER ceilings never change: no previous-year pass.
        var read = await ReadAsync(year, scope, today, ct, withPreviousYear: false);
        if (read.TryGetFailure(out var failure)) return Result.Failure<Rental2044View>(failure!);
        read.TryGetValue(out var r);
        var (lines, people) = (r!.Lines, r.People);
        var (inputs, notices, columns) = Assemble(year, lines, r.Effective, people);
        var regime = TaxCalculator.Compute(inputs).RentalRegime;
        var t = Rental2044.Totals(columns, TaxRules.For(year).DeficitCap(inputs.RentalEnergyWorks, inputs.RentalCharges));
        var micro = regime == RentalRegime.Micro;
        // The notices of RealEstate's items only: its rental lines and the ones it left out of the 2044.
        var rental = lines.Where(l => l.Code.StartsWith("rental_", StringComparison.Ordinal) || l.Category == TaxLineCodes.Rental2044)
            .Select(l => l.Item).ToHashSet();
        return Result.Success(new Rental2044View(
            year, TaxWire.Wire(regime), year >= today.Year,
            columns.Select(c => new Rental2044PropertyView(new ItemId(c.Property), c.Name, new (string Line, decimal Amount)[]
            {
                ("211", c.L211), ("215", c.L215), ("221", c.L221), ("222", c.L222), ("223", c.L223), ("224", c.L224),
                ("227", c.L227), ("229", c.L229), ("230", c.L230), ("240", c.L240), ("250", c.L250), ("263", c.L263),
            }.Select(l => new Rental2044LineView(l.Line, Amount(l.Amount))).ToList())).ToList(),
            new Rental2044TotalsView(
                Amount(t.Receipts), Amount(t.Charges), Amount(t.Interest), Amount(t.Result), Amount(t.InterestDeficit), Amount(t.ChargesDeficit),
                Amount(t.GlobalDeficit), Amount(t.CarriedDeficit), Amount(micro ? 0m : t.Taxable), Amount(micro ? 0m : t.CarriedDeficit),
                Amount(micro ? 0m : t.GlobalDeficit), Amount(micro ? t.Receipts : 0m)),
            lines.Where(l => l.Code == TaxLineCodes.RentalToCheck).OrderBy(l => l.On).ThenBy(l => l.Name, StringComparer.Ordinal)
                .Select(l => new Rental2044CheckView(l.Item, l.Name, l.On!.Value, l.Category!, l.Note, Amount(l.Amount))).ToList(),
            notices.Where(n => rental.Contains(n.Item) && n.Code != TaxLineCodes.RentalToCheck).ToList()));
    }

    /// <summary>
    /// What an estimate reads: <c>Stored</c> is the profile as saved (what the profile view shows), <c>Effective</c> the one the
    /// calculation runs on, the previous year's notices laid over it (<c>Carry</c>), and <c>YearNotices</c> the year's own notices the
    /// reader sees in the scope, to compare with.
    /// </summary>
    private sealed record Read(
        IReadOnlyList<TaxLine> Lines, StoredTaxProfile? Stored, IReadOnlyList<PersonInfo> People, IReadOnlyDictionary<Guid, decimal> PreviousYearIncome,
        TaxProfile Effective, TaxCarryOver? Carry, IReadOnlyList<StoredTaxAssessment> YearNotices, (decimal N1, decimal N2)? ReferenceBefore);

    /// <summary>
    /// Every module's lines for the year, the year's profile (with the notices of the year before laid over it) and the household, then
    /// (<paramref name="withPreviousYear"/>) each adult's income of the year before, the basis of an estimated PER ceiling.
    /// </summary>
    private async Task<Result<Read>> ReadAsync(
        int year, VisibilityScope scope, NodaTime.LocalDate today, CancellationToken ct, bool withPreviousYear = true)
    {
        var reader = actor.Current!.PersonId!.Value;
        var collected = await Contributions.CollectAsync(contributions, c => c.TaxLinesAsync(reader, scope, year, today, ct));
        if (collected.TryGetFailure(out var failure))
            return Result.Failure<Read>(failure!);
        collected.TryGetValue(out var parts);
        var lines = parts!.SelectMany(p => p).ToList();
        var stored = await store.FindAsync(year, ct);
        // Tax-per spec §4.1: a notice's ceiling belongs to its own year; the other fields carry forward from an earlier profile.
        if (stored is { } earlier && earlier.Year != year)
            stored = earlier with { Profile = earlier.Profile with { PerCeilings = null, PerCeilingsExpiring = null } };
        var people = await persons.ListAsync(ct);
        var adults = Adults(people);
        // Tax assessments spec §5: the notices the reader declares, in the scope as the notices' list gives them (Foyer: both adults declare, or the only active adult).
        var read = (await assessments.ListForAsync(reader, null, ct)).Where(n => n.Assessment.Year >= year - 2 && n.Assessment.Year <= year)
            .Where(n => scope != VisibilityScope.Household || TaxAssessmentAccess.InHousehold(n.Assessment, people)).ToList();
        var notices = read.Where(n => n.Assessment.Year >= year - 1).ToList();
        // #234: the reference incomes of the two years before, for the high-income contribution's smoothing; two separate notices of a
        // year add up. Without a figure for both years, none.
        decimal? Reference(int y) => read.Where(n => n.Assessment.Year == y).Select(n => n.Assessment.ReferenceIncome).Where(v => v is not null)
            .Aggregate((decimal?)null, (sum, v) => (sum ?? 0m) + v);
        (decimal, decimal)? referenceBefore = Reference(year - 1) is { } n1 && Reference(year - 2) is { } n2 ? (n1, n2) : null;
        var carry = TaxCarryOver.From(year, notices.Where(n => n.Assessment.Year == year - 1).Select(n => n.Assessment), adults.Select(a => a.Id.Value).ToList());
        var effective = carry?.Apply(stored?.Profile ?? TaxProfile.Empty) ?? stored?.Profile ?? TaxProfile.Empty;
        IReadOnlyDictionary<Guid, decimal> previous = new Dictionary<Guid, decimal>();
        // A notice ceiling for every adult needs no estimate: the year before is read only when one is estimated.
        if (withPreviousYear && adults.Any(a => effective.PerCeilings?.ContainsKey(a.Id.Value) != true))
        {
            var income = await PreviousYearIncomeAsync(year, scope, today, people, ct);
            if (income.TryGetFailure(out var previousFailure))
                return Result.Failure<Read>(previousFailure!);
            income.TryGetValue(out var value);
            previous = value!;
        }
        return Result.Success(new Read(lines, stored, people, previous, effective, carry, notices.Where(n => n.Assessment.Year == year).ToList(), referenceBefore));
    }

    /// <summary>
    /// Tax-per spec §4.2: each adult's taxable income of the year before with the exempt overtime after the allowance (the professional
    /// income the PER ceiling reads, unemployment included: brochure IR 2026 § 232), by person id: the same lines (same reader and scope) and the
    /// profile standing for that year, assembled and computed without PER and without a previous-year pass of its own. Before the first
    /// year of the rules the calculation runs on the first year's, as the PASS does. Issue #438: kept for the rest of the scope by
    /// reader, year and scope once the year before is over on <paramref name="today"/>, so a period's months read it once per year.
    /// </summary>
    private async Task<Result<IReadOnlyDictionary<Guid, decimal>>> PreviousYearIncomeAsync(
        int year, VisibilityScope scope, NodaTime.LocalDate today, IReadOnlyList<PersonInfo> people, CancellationToken ct)
    {
        var reader = actor.Current!.PersonId!.Value;
        // ponytail: relies on ITaxContribution's contract (a past year's lines ignore the reading date), which only
        // TaxPreviousYearTests guards; key it by the date as well if a contribution ever needs it for a past year.
        var key = (reader, year, scope);
        var past = year <= today.Year;
        if (past && previousYears.TryGetValue(key, out var kept)) return Result.Success(kept);
        var collected = await Contributions.CollectAsync(contributions, c => c.TaxLinesAsync(reader, scope, year - 1, today, ct));
        if (collected.TryGetFailure(out var failure))
            return Result.Failure<IReadOnlyDictionary<Guid, decimal>>(failure!);
        collected.TryGetValue(out var parts);
        var stored = await store.FindAsync(year - 1, ct);
        var (inputs, _, _) = Assemble(year - 1, parts!.SelectMany(p => p).ToList(), stored?.Profile ?? TaxProfile.Empty, people);
        var result = TaxCalculator.Compute(inputs with
        {
            Year = Math.Max(inputs.Year, TaxRules.FirstYear),
            Declarants = inputs.Declarants.Select(d => d with { PerContributions = 0m }).ToList(),
        });
        // A pension is no professional income: the ceiling leaves it out.
        IReadOnlyDictionary<Guid, decimal> income = Adults(people)
            .Zip(result.Declarants, (adult, d) => (Id: adult.Id.Value, Income: d.Taxable - d.Pensions + d.PensionAllowance + d.ExemptNet)).ToDictionary(x => x.Id, x => x.Income);
        if (past) previousYears[key] = income;
        return Result.Success(income);
    }

    private readonly Dictionary<(PersonId Reader, int Year, VisibilityScope Scope), IReadOnlyDictionary<Guid, decimal>> previousYears = [];

    internal static Failure? CheckYear(int year, NodaTime.LocalDate today)
        => TaxChecks.YearOk(year, today.Year)
            ? null
            : new Failure(TaxCodes.InvalidYear, $"The estimate covers the revenues of {TaxRules.FirstYear} to {today.Year + 1}.", null);

    internal static Failure? CheckProfile(TaxProfile p)
        => TaxChecks.ProfileOk(p) ? null : new Failure(TaxCodes.InvalidProfile,
            "Statuses are non_executive, executive or public; a contribution rate is between 0 and 60 %; the regime micro or real; a Pinel commitment starts at 6 or 9 years and lasts 6, 9 or 12, once per property; amounts are not negative; alimony paid to adult children comes with their number, 1 to 20; the children in alternate custody and the disabled ones are 0 to 20, among the dependents; the part of a PER ceiling lost on 31 December comes with that ceiling and does not exceed it; a department is a code from 01 to 95, 2A, 2B or 971 to 976.", null);

    internal static Failure? CheckInputs(TaxInputs inputs)
        => TaxChecks.InputsOk(inputs) ? null : new Failure(TaxCodes.InvalidInputs,
            "One or two declarants, their entered net taxable income, PER ceiling, previous-year income and expiring ceiling not negative; dependents between 0 and 20, the children under 6 among them; adult children receiving alimony between 0 and 20; the children in alternate custody and the disabled ones among the dependents.", null);

    internal static string Percent(decimal fraction) => (fraction * 100).ToString("0.##", CultureInfo.InvariantCulture);

    internal static string Amount(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>An amount in euros as the views give it.</summary>
    internal static DashboardMoneyView Euros(decimal amount) => new(Amount(amount), Currency.Eur.Code);
}
