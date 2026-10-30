using System.Globalization;
using Nid.Core.Application.Dashboard;
using Nid.Core.Contracts;
using Nid.Core.Domain.Tax;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Tax;

/// <summary>Tax estimate spec §8: the year's profile, the inputs read from the modules, the result and what was left out.</summary>
public sealed record GetTaxEstimateQuery(int? Year, VisibilityScope Scope) : IQuery<TaxEstimateView>;

/// <summary>Tax estimate spec §7: the household's declarations for the year, then the estimate they give.</summary>
public sealed record SaveTaxProfileCommand(int Year, TaxProfile Profile, VisibilityScope Scope) : ICommand<TaxEstimateView>;

/// <summary>Rental 2044 spec §5: the form of a revenue year, last year's by default (the one declared).</summary>
public sealed record GetRental2044Query(int? Year, VisibilityScope Scope) : IQuery<Rental2044View>;

/// <summary>Tax estimate spec §8: the calculation of given inputs; nothing is read nor stored.</summary>
public sealed record SimulateTaxQuery(TaxInputs Inputs) : IQuery<TaxResultView>;

/// <summary>Sale tax spec §6: with <c>ItemId</c>, that item's page among everything the reader sees; without, the scope's total.</summary>
public sealed record GetSaleTaxQuery(VisibilityScope Scope, ItemId? ItemId) : IQuery<SaleTaxView>, IOptionalItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

public static class TaxCodes
{
    public const string InvalidYear = "tax.invalid_year";
    public const string InvalidProfile = "tax.invalid_profile";
    public const string InvalidInputs = "tax.invalid_inputs";
}

[RequestHandler<GetTaxEstimateQuery, TaxEstimateView>]
public sealed class GetTaxEstimateHandler(TaxEstimates estimates, IHouseholdCalendar calendar) : IRequestHandler<GetTaxEstimateQuery, TaxEstimateView>
{
    public async ValueTask<Result<TaxEstimateView>> HandleAsync(GetTaxEstimateQuery query, CancellationToken ct = default)
    {
        var today = await calendar.TodayAsync(ct);
        var year = query.Year ?? today.Year;
        return TaxEstimates.CheckYear(year, today) is { } failure
            ? Result.Failure<TaxEstimateView>(failure)
            : await estimates.EstimateAsync(year, query.Scope, today, ct);
    }
}

[RequestHandler<GetRental2044Query, Rental2044View>]
public sealed class GetRental2044Handler(TaxEstimates estimates, IHouseholdCalendar calendar) : IRequestHandler<GetRental2044Query, Rental2044View>
{
    public async ValueTask<Result<Rental2044View>> HandleAsync(GetRental2044Query query, CancellationToken ct = default)
    {
        var today = await calendar.TodayAsync(ct);
        var year = query.Year ?? today.Year - 1;
        return TaxEstimates.CheckYear(year, today) is { } failure
            ? Result.Failure<Rental2044View>(failure)
            : await estimates.Rental2044Async(year, query.Scope, today, ct);
    }
}

[RequestHandler<GetSaleTaxQuery, SaleTaxView>]
public sealed class GetSaleTaxHandler(SaleTaxEstimates estimates, IHouseholdCalendar calendar, IActorAccessor actor)
    : IRequestHandler<GetSaleTaxQuery, SaleTaxView>
{
    public async ValueTask<Result<SaleTaxView>> HandleAsync(GetSaleTaxQuery query, CancellationToken ct = default)
        => await estimates.ComputeAsync(actor.Current!.PersonId!.Value, query.Scope, query.ItemId, await calendar.TodayAsync(ct), ct);
}

[RequestHandler<SaveTaxProfileCommand, TaxEstimateView>]
public sealed class SaveTaxProfileHandler(TaxEstimates estimates, ITaxProfileStore store, IHouseholdCalendar calendar)
    : IRequestHandler<SaveTaxProfileCommand, TaxEstimateView>
{
    public async ValueTask<Result<TaxEstimateView>> HandleAsync(SaveTaxProfileCommand command, CancellationToken ct = default)
    {
        var today = await calendar.TodayAsync(ct);
        if ((TaxEstimates.CheckYear(command.Year, today) ?? TaxEstimates.CheckProfile(command.Profile)) is { } failure)
            return Result.Failure<TaxEstimateView>(failure);
        await store.SaveAsync(command.Year, command.Profile, ct);
        return await estimates.EstimateAsync(command.Year, command.Scope, today, ct);
    }
}

[RequestHandler<SimulateTaxQuery, TaxResultView>]
public sealed class SimulateTaxHandler(IHouseholdCalendar calendar) : IRequestHandler<SimulateTaxQuery, TaxResultView>
{
    public async ValueTask<Result<TaxResultView>> HandleAsync(SimulateTaxQuery query, CancellationToken ct = default)
    {
        var inputs = query.Inputs;
        if (TaxEstimates.CheckYear(inputs.Year, await calendar.TodayAsync(ct)) is { } failure)
            return Result.Failure<TaxResultView>(failure);
        if (inputs.Declarants.Count is < 1 or > 2 || inputs.Declarants.Any(d => d.ContributionRate is { } r && !TaxEstimates.RateOk(r) || d.SalaryTaxable < 0m || d.PerCeiling < 0m || d.PreviousYearIncome < 0m || d.PerExpiring < 0m) || inputs.Dependents is < 0 or > 20 || inputs.ChildrenUnder6 < 0 || inputs.ChildrenUnder6 > inputs.Dependents)
            return Result.Failure<TaxResultView>(new Failure(TaxCodes.InvalidInputs,
                "One or two declarants, their entered net taxable income, PER ceiling, previous-year income and expiring ceiling not negative; dependents between 0 and 20, the children under 6 among them.", null));
        return Result.Success(TaxResultView.Of(TaxCalculator.Compute(inputs)));
    }
}

/// <summary>Spec §6 and §7: reads every module's lines, applies the profile, computes.</summary>
public sealed class TaxEstimates(
    IEnumerable<ITaxContribution> contributions, ITaxProfileStore store, ITaxAssessmentStore assessments, IPersonRegistry persons, IActorAccessor actor)
{
    public async Task<Result<TaxEstimateView>> EstimateAsync(int year, VisibilityScope scope, NodaTime.LocalDate today, CancellationToken ct)
    {
        var read = await ReadAsync(year, scope, today, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<TaxEstimateView>(failure!);
        read.TryGetValue(out var r);
        var (lines, stored, people, previous) = (r!.Lines, r.Stored, r.People, r.PreviousYearIncome);
        var (inputs, notices, _) = Assemble(year, lines, r.Effective, people, previous);
        var adults = Adults(people);
        var result = TaxCalculator.Compute(inputs);
        // Tax-per spec §4.5: the PERs each declarant's contributions were read from.
        IReadOnlyList<IReadOnlyList<string>> perEnvelopes = adults
            .Select(a => (IReadOnlyList<string>)lines.Where(l => l.Code == TaxLineCodes.PerContribution && l.Person == a.Id)
                .Select(l => l.Name).Distinct().Order(StringComparer.Ordinal).ToList()).ToList();
        return Result.Success(new TaxEstimateView(
            year, TaxProfileView.Of(stored?.Profile ?? TaxProfile.Empty), stored is { } s && s.Year != year ? s.Year : null,
            TaxInputsView.Of(inputs, perEnvelopes), TaxResultView.Of(result, adults.Select(a => a.Id.Value).ToList()), notices,
            adults.Select(a => new TaxAdultView(a.Id.Value, a.DisplayName)).ToList(),
            people.Count(p => p.Kind == PersonKind.Dependent),
            lines.Where(l => l.Code == TaxLineCodes.RentalProperty).Select(l => new TaxPropertyView(l.Item, l.Name, l.On!.Value.Year)).ToList(),
            lines.Where(l => l.Code == TaxLineCodes.SalaryDetail && l.Person is { } p && adults.Any(a => a.Id == p))
                .OrderBy(l => l.Person!.Value.Value).ThenBy(l => l.On).ThenBy(l => l.Name, StringComparer.Ordinal)
                .Select(l => new TaxSalaryDetailView(l.Person!.Value.Value, l.Item, l.Name, l.On!.Value, l.Note, l.Category!, Amount(l.Amount))).ToList(),
            adults.Select(a => (a.Id.Value, Rate: ObservedRate(lines, a.Id))).Where(x => x.Rate is not null)
                .ToDictionary(x => x.Value, x => Percent(x.Rate!.Value)),
            r.Carry is { } carry ? TaxCarryView.Of(carry) : null, GapsOf(r.YearNotices, result, adults.Select(a => a.Id.Value).ToList())));
    }

    /// <summary>Tax assessments spec §6: the gaps of the year's notice whose declarants are the estimate's adults, if there is one.</summary>
    private static TaxGapsView? GapsOf(IEnumerable<StoredTaxAssessment> yearNotices, TaxResult result, IReadOnlyList<Guid> adults)
        => yearNotices.Select(n => (n.Id, Gaps: TaxGaps.Of(n.Assessment, result, adults))).Where(x => x.Gaps is not null)
            .Select(x => TaxGapsView.Of(x.Id, x.Gaps!)).FirstOrDefault();

    /// <summary>
    /// Tax-per spec §3.4: the contributions a person's payslips show, as a fraction of the gross: what the gross loses to reach the net
    /// taxable income, the non-deductible CSG added back (as the calculator adds it), over the gross. Only the payments holding both
    /// figures count; null when there is none.
    /// </summary>
    private static decimal? ObservedRate(IReadOnlyList<TaxLine> lines, PersonId adult)
    {
        decimal Sum(string code) => lines.Where(l => l.Code == code && l.Person == adult).Sum(l => l.Amount);
        var gross = Sum(TaxLineCodes.SalaryRateGross);
        return gross > 0m ? Math.Round((gross - Sum(TaxLineCodes.SalaryRateTaxable) + TaxRules.NonDeductibleCsg * gross) / gross, 4, MidpointRounding.AwayFromZero) : null;
    }

    /// <summary>The year's inputs as the estimate computes them (the property purchase scenario replays the engine on them).</summary>
    public async Task<Result<TaxInputs>> InputsAsync(int year, VisibilityScope scope, NodaTime.LocalDate today, CancellationToken ct)
    {
        var read = await ReadAsync(year, scope, today, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<TaxInputs>(failure!);
        read.TryGetValue(out var r);
        return Result.Success(Assemble(year, r!.Lines, r.Effective, r.People, r.PreviousYearIncome).Inputs);
    }

    /// <summary>
    /// The year's result alone, as <see cref="EstimateAsync"/> computes it. Kept for the rest of the scope (a request): the dashboard asks
    /// twice, for « Impôts » and the PER deadline. Keyed by the reader too, as a job's scope may read for several.
    /// </summary>
    public async Task<Result<TaxResult>> ComputeAsync(int year, VisibilityScope scope, NodaTime.LocalDate today, CancellationToken ct)
    {
        var key = (actor.Current!.PersonId!.Value, year, scope, today);
        if (computed.TryGetValue(key, out var kept)) return Result.Success(kept);
        var read = await ReadAsync(year, scope, today, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<TaxResult>(failure!);
        read.TryGetValue(out var r);
        var (inputs, _, _) = Assemble(year, r!.Lines, r.Effective, r.People, r.PreviousYearIncome);
        return Result.Success(computed[key] = TaxCalculator.Compute(inputs));
    }

    private readonly Dictionary<(PersonId Reader, int Year, VisibilityScope Scope, NodaTime.LocalDate Today), TaxResult> computed = [];

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
            year, Wire(regime), year >= today.Year,
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
        TaxProfile Effective, TaxCarryOver? Carry, IReadOnlyList<StoredTaxAssessment> YearNotices);

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
        var notices = (await assessments.ListForAsync(reader, null, ct)).Where(n => n.Assessment.Year == year - 1 || n.Assessment.Year == year)
            .Where(n => scope != VisibilityScope.Household || TaxAssessmentAccess.InHousehold(n.Assessment, people)).ToList();
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
        return Result.Success(new Read(lines, stored, people, previous, effective, carry, notices.Where(n => n.Assessment.Year == year).ToList()));
    }

    /// <summary>
    /// Tax-per spec §4.2: each adult's taxable income of the year before with the exempt overtime after the allowance (the professional
    /// income the PER ceiling reads, unemployment included: brochure IR 2026 § 232), by person id: the same lines (same reader and scope) and the
    /// profile standing for that year, assembled and computed without PER and without a previous-year pass of its own. Before the first
    /// year of the rules the calculation runs on the first year's, as the PASS does.
    /// </summary>
    private async Task<Result<IReadOnlyDictionary<Guid, decimal>>> PreviousYearIncomeAsync(
        int year, VisibilityScope scope, NodaTime.LocalDate today, IReadOnlyList<PersonInfo> people, CancellationToken ct)
    {
        var reader = actor.Current!.PersonId!.Value;
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
        return Result.Success<IReadOnlyDictionary<Guid, decimal>>(
            Adults(people).Zip(result.Declarants, (adult, d) => (Id: adult.Id.Value, Income: d.Taxable + d.ExemptNet)).ToDictionary(x => x.Id, x => x.Income));
    }

    /// <summary>The adults who declare, in a stable order: the household's first two.</summary>
    public static IReadOnlyList<PersonInfo> Adults(IReadOnlyList<PersonInfo> people)
        => people.Where(p => p.Kind == PersonKind.Adult).OrderBy(p => p.DisplayName, StringComparer.Ordinal).ThenBy(p => p.Id.Value).Take(2).ToList();

    /// <summary>Rental 2044 spec §4: the estimate's rental figures are the form's columns, also returned for the form.</summary>
    public static (TaxInputs Inputs, IReadOnlyList<TaxNoticeView> Notices, IReadOnlyList<Rental2044Column> Columns) Assemble(
        int year, IReadOnlyList<TaxLine> lines, TaxProfile profile, IReadOnlyList<PersonInfo> people,
        IReadOnlyDictionary<Guid, decimal>? previousYearIncome = null)
    {
        var notices = new List<TaxNoticeView>();
        void Notice(string code, TaxLine line) => notices.Add(new TaxNoticeView(code, line.Item, line.Name, Amount(line.Amount)));
        var adults = Adults(people);
        var declarants = new List<TaxDeclarant>();
        foreach (var adult in adults)
        {
            var status = profile.Statuses.TryGetValue(adult.Id.Value, out var wire) && TryStatus(wire, out var s) ? s : EmploymentStatus.NonExecutive;
            var own = lines.Where(l => l.Person == adult.Id).ToList();
            decimal Sum(string code) => own.Where(l => l.Code == code).Sum(l => l.Amount);
            // A net amount (a phase without its gross, a one-off) goes back to its gross at the status's rate.
            decimal? rate = profile.ContributionRates?.TryGetValue(adult.Id.Value, out var r) == true ? r : null;
            decimal? expenses = profile.ActualExpenses?.TryGetValue(adult.Id.Value, out var e) == true ? e : null;
            var unused = profile.PerUnusedCeilings?.GetValueOrDefault(adult.Id.Value) ?? 0m;
            var declarant = new TaxDeclarant(adult.DisplayName, 0m, status, Sum(TaxLineCodes.OtherTaxable), Sum(TaxLineCodes.PerContribution), rate, expenses, unused,
                Sum(TaxLineCodes.SalaryTaxable), PerCeiling: profile.PerCeilings?.TryGetValue(adult.Id.Value, out var ceiling) == true ? ceiling : null,
                PreviousYearIncome: previousYearIncome?.GetValueOrDefault(adult.Id.Value) ?? 0m,
                PerExpiring: profile.PerCeilingsExpiring?.GetValueOrDefault(adult.Id.Value) ?? 0m,
                ExemptIncome: profile.ExemptIncomes?.GetValueOrDefault(adult.Id.Value) ?? 0m);
            // The profile's rate against the payslips': the notice sits on the first salary holding both figures (the adult is no item).
            if (rate is { } declared && ObservedRate(lines, adult.Id) is { } observed && Math.Abs(declared - observed) > 0.03m
                && own.FirstOrDefault(l => l.Code == TaxLineCodes.SalaryRateGross) is { } source)
                notices.Add(new TaxNoticeView(TaxWarnings.ContributionRateDiffers, source.Item, source.Name, Percent(observed)));
            var gross = Sum(TaxLineCodes.SalaryGross) + (Sum(TaxLineCodes.SalaryNet) + Sum(TaxLineCodes.SalaryOneOff)) / (1 - declarant.EffectiveContributionRate);
            declarants.Add(declarant with { SalaryGross = gross });
        }
        if (declarants.Count == 0)
            declarants.Add(new TaxDeclarant("", 0m, EmploymentStatus.NonExecutive, 0m, 0m));

        string[] personal = [TaxLineCodes.SalaryTaxable, TaxLineCodes.SalaryGross, TaxLineCodes.SalaryNet, TaxLineCodes.SalaryOneOff, TaxLineCodes.OtherTaxable, TaxLineCodes.PerContribution];
        foreach (var line in lines.Where(l => !Informative.Contains(l.Code)))
        {
            if (personal.Contains(line.Code) && !adults.Any(a => a.Id == line.Person) && line.Amount != 0)
                Notice("not_a_declarant", line);
            else if (line.Code == TaxLineCodes.SalaryNet)
                Notice("missing_gross", line);
            else if (line.Code == TaxLineCodes.Unsupported && line.Amount != 0)
                Notice("unsupported_income", line);
            else if (line.Code == TaxLineCodes.Withdrawal)
                Notice("withdrawal_not_estimated", line);
            else if (line.Code is TaxLineCodes.OtherCurrency or TaxLineCodes.RentalChargesRemoved or TaxLineCodes.RentalFeeNotCounted)
                Notice(line.Code, line);
        }

        // The works and other expenses the estimate does not count, one notice per property (the 2044 lists them itself).
        foreach (var property in lines.Where(l => l.Code == TaxLineCodes.RentalToCheck).GroupBy(l => (l.Item, l.Name)))
            notices.Add(new TaxNoticeView(TaxLineCodes.RentalToCheck, property.Key.Item, property.Key.Name, Amount(property.Sum(l => l.Amount))));

        // Tax-per spec §4.4: a PER holding a value that received nothing this year, one notice per envelope (not per owner).
        foreach (var envelope in lines.Where(l => l.Code == TaxLineCodes.PerNoContribution && adults.Any(a => a.Id == l.Person)).GroupBy(l => (l.Item, l.Name)))
            notices.Add(new TaxNoticeView("per_without_contributions", envelope.Key.Item, envelope.Key.Name, Amount(0m)));

        decimal All(string code) => lines.Where(l => l.Code == code).Sum(l => l.Amount);
        var rentals = lines.Where(l => l.Code == TaxLineCodes.RentalProperty).ToList();
        var pinel = 0m;
        foreach (var commitment in profile.Pinel)
        {
            if (rentals.FirstOrDefault(l => l.Item.Value == commitment.Property) is not { } property)
                continue; // a property out of the scope or no longer rented counts nothing
            var acquired = property.On!.Value.Year;
            pinel += Pinel.AnnualReduction(Pinel.Base(property.Amount, property.Surface), acquired, commitment.Plus,
                commitment.InitialYears, commitment.TotalYears, commitment.StartYear ?? acquired, year);
        }

        var columns = Rental2044.Columns(lines.Where(l => FormLines.Contains(l.Code)).GroupBy(l => l.Item).Select(g =>
        {
            decimal L(string code) => g.Where(l => l.Code == code).Sum(l => l.Amount);
            return new Rental2044Input(g.Key.Value, g.First().Name, L(TaxLineCodes.Rental211), L(TaxLineCodes.Rental221), L(TaxLineCodes.Rental222),
                L(TaxLineCodes.Rental223), L(TaxLineCodes.Rental224), L(TaxLineCodes.Rental227), L(TaxLineCodes.Rental229), L(TaxLineCodes.Rental250));
        }));

        var dependents = profile.Dependents ?? people.Count(p => p.Kind == PersonKind.Dependent);
        var inputs = new TaxInputs(
            year, declarants, dependents, Math.Min(profile.ChildrenUnder6, dependents),
            columns.Sum(c => c.L215), columns.Sum(c => c.L250), columns.Sum(c => c.L240),
            TryRegime(profile.RentalRegime, out var regime) ? regime : null,
            All(TaxLineCodes.CapitalIncome), All(TaxLineCodes.NonTaxable), pinel,
            profile.ChildcareOutsideHome, profile.HomeEmployment, profile.Donations, profile.DonationsAid, profile.RentalDeficitCarried, profile.PerPooling,
            profile.DonationsAidLate, profile.RentalEnergyWorks);
        return (inputs, notices, columns);
    }

    /// <summary>Lines shown, never summed nor noticed: a payment's detail and the figures of the observed rate.</summary>
    private static readonly HashSet<string> Informative = [TaxLineCodes.SalaryDetail, TaxLineCodes.SalaryRateGross, TaxLineCodes.SalaryRateTaxable];

    private static readonly HashSet<string> FormLines =
    [
        TaxLineCodes.Rental211, TaxLineCodes.Rental221, TaxLineCodes.Rental222, TaxLineCodes.Rental223,
        TaxLineCodes.Rental224, TaxLineCodes.Rental227, TaxLineCodes.Rental229, TaxLineCodes.Rental250,
    ];

    internal static Failure? CheckYear(int year, NodaTime.LocalDate today)
        => year < TaxRules.FirstYear || year > today.Year + 1
            ? new Failure(TaxCodes.InvalidYear, $"The estimate covers the revenues of {TaxRules.FirstYear} to {today.Year + 1}.", null)
            : null;

    internal static Failure? CheckProfile(TaxProfile p)
    {
        var ok = p.Statuses.Values.All(s => TryStatus(s, out _))
                 && p.Dependents is null or (>= 0 and <= 20) && p.ChildrenUnder6 is >= 0 and <= 20
                 && (p.RentalRegime is null || TryRegime(p.RentalRegime, out _))
                 && p.Pinel.All(c => c.InitialYears is 6 or 9 && c.TotalYears is 6 or 9 or 12 && c.TotalYears >= c.InitialYears
                                     && c.StartYear is null or (>= 2014 and <= 2040))
                 && p.Pinel.Select(c => c.Property).Distinct().Count() == p.Pinel.Count
                 && new[] { p.ChildcareOutsideHome, p.HomeEmployment, p.Donations, p.DonationsAid, p.RentalDeficitCarried, p.DonationsAidLate, p.RentalEnergyWorks }.All(a => a is >= 0 and <= 10_000_000m)
                 && (p.ContributionRates?.Values.All(RateOk) ?? true) && (p.ActualExpenses?.Values.All(a => a is >= 0 and <= 10_000_000m) ?? true)
                 && (p.PerUnusedCeilings?.Values.All(a => a is >= 0 and <= 10_000_000m) ?? true)
                 && (p.ExemptIncomes?.Values.All(a => a is >= 0 and <= 10_000_000m) ?? true)
                 && (p.PerCeilings?.Values.All(a => a is >= 0 and <= 10_000_000m) ?? true)
                 && (p.PerCeilingsExpiring?.All(e => e.Value is >= 0 and <= 10_000_000m && p.PerCeilings?.TryGetValue(e.Key, out var ceiling) == true && e.Value <= ceiling) ?? true);
        return ok ? null : new Failure(TaxCodes.InvalidProfile,
            "Statuses are non_executive, executive or public; a contribution rate is between 0 and 60 %; the regime micro or real; a Pinel commitment starts at 6 or 9 years and lasts 6, 9 or 12, once per property; amounts are not negative; the part of a PER ceiling lost on 31 December comes with that ceiling and does not exceed it.", null);
    }

    /// <summary>A payslip's employee contributions: between 0 and 60 % of the gross, as a fraction.</summary>
    public static bool RateOk(decimal rate) => rate is >= 0m and <= 0.60m;

    public static bool TryStatus(string? wire, out EmploymentStatus status)
    {
        (var ok, status) = wire switch
        {
            "non_executive" => (true, EmploymentStatus.NonExecutive),
            "executive" => (true, EmploymentStatus.Executive),
            "public" => (true, EmploymentStatus.Public),
            _ => (false, default),
        };
        return ok;
    }

    public static string Wire(EmploymentStatus status) => status switch
    {
        EmploymentStatus.Executive => "executive",
        EmploymentStatus.Public => "public",
        _ => "non_executive",
    };

    public static bool TryRegime(string? wire, out RentalRegime regime)
    {
        (var ok, regime) = wire switch { "micro" => (true, RentalRegime.Micro), "real" => (true, RentalRegime.Real), _ => (false, default) };
        return ok;
    }

    public static string Wire(RentalRegime regime) => regime == RentalRegime.Real ? "real" : "micro";

    internal static string Percent(decimal fraction) => (fraction * 100).ToString("0.##", CultureInfo.InvariantCulture);

    internal static string Amount(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);
}

/// <summary>
/// The dashboard's « Impôts »: a twelfth of this year's estimated total tax, in euros (the estimate reads euro lines only), as a
/// commitment so the month's rest is what is left after it. A refund costs nothing a month: no source then.
/// </summary>
public sealed class TaxCommitments(TaxEstimates estimates) : ICommitmentsContribution
{
    public async Task<Result<CommitmentsPart>> CommitmentsAsync(PersonId reader, VisibilityScope scope, NodaTime.LocalDate today, CancellationToken ct)
    {
        var result = await estimates.ComputeAsync(today.Year, scope, today, ct);
        if (result.TryGetFailure(out var failure)) return Result.Failure<CommitmentsPart>(failure!);
        result.TryGetValue(out var r);
        var monthly = Math.Max(0m, r!.TotalTax) / 12;
        // Nothing to pay puts no euro entry, which would ask for a rate in a household of another currency.
        var amortized = monthly > 0m ? new Dictionary<Currency, decimal> { [Currency.Eur] = monthly } : new Dictionary<Currency, decimal>();
        return Result.Success(new CommitmentsPart(CommitmentSources.Tax, amortized.Count, amortized, new Dictionary<Currency, decimal>()));
    }
}

/// <summary>
/// Tax-per spec §4.6: « À venir » on 31 December, once it falls in the window. For each declarant the reader sees (all in Foyer, the
/// reader alone in Moi), the PER ceiling left to pay in and the part of it lost that day; under pooling the ceiling left is the
/// household's, one event in Foyer only. In euros, about no item; nothing when nothing is left.
/// </summary>
public sealed class PerDeadline(TaxEstimates estimates, IPersonRegistry persons)
{
    /// <summary>The pooled event's label, as it has no person's name.</summary>
    public const string Household = "household";

    public async Task<Result<IReadOnlyList<DashboardEventView>>> EventsAsync(
        PersonId reader, VisibilityScope scope, NodaTime.LocalDate today, NodaTime.LocalDate until, CancellationToken ct)
    {
        var on = new NodaTime.LocalDate(today.Year, 12, 31);
        if (on > until) return Result.Success<IReadOnlyList<DashboardEventView>>([]);
        var computed = await estimates.ComputeAsync(today.Year, scope, today, ct);
        if (computed.TryGetFailure(out var failure)) return Result.Failure<IReadOnlyList<DashboardEventView>>(failure!);
        computed.TryGetValue(out var r);
        var pooled = r!.Per?.Pooled == true;
        DashboardEventView Event(string kind, string name, string? label, decimal amount)
            => new(on, kind, null, "tax", name, label, DashboardMath.Money(amount, Currency.Eur));

        var events = new List<DashboardEventView>();
        foreach (var (adult, d) in TaxEstimates.Adults(await persons.ListAsync(ct)).Zip(r.Declarants))
        {
            if (d.Per is not { } per || scope == VisibilityScope.Me && adult.Id != reader) continue;
            if (!pooled && per.Remaining > 0m) events.Add(Event(UpcomingKinds.PerDeadline, adult.DisplayName, null, per.Remaining));
            // What keeps the expiring ceiling: the year's own ceiling is used first (spec §4.3).
            if (per.Minimum > 0m) events.Add(Event(UpcomingKinds.PerCeilingExpiring, adult.DisplayName, null, per.Minimum));
        }
        if (pooled && scope == VisibilityScope.Household && r.Per!.Remaining > 0m)
            events.Add(Event(UpcomingKinds.PerDeadline, "", Household, r.Per.Remaining));
        return Result.Success<IReadOnlyList<DashboardEventView>>(events);
    }
}
