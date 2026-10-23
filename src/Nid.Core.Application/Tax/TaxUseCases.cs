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
        if (inputs.Declarants.Count is < 1 or > 2 || inputs.Declarants.Any(d => d.ContributionRate is { } r && !TaxEstimates.RateOk(r)) || inputs.Dependents is < 0 or > 20 || inputs.ChildrenUnder6 < 0 || inputs.ChildrenUnder6 > inputs.Dependents)
            return Result.Failure<TaxResultView>(new Failure(TaxCodes.InvalidInputs,
                "One or two declarants; dependents between 0 and 20, the children under 6 among them.", null));
        return Result.Success(TaxResultView.Of(TaxCalculator.Compute(inputs)));
    }
}

/// <summary>Spec §6 and §7: reads every module's lines, applies the profile, computes.</summary>
public sealed class TaxEstimates(IEnumerable<ITaxContribution> contributions, ITaxProfileStore store, IPersonRegistry persons, IActorAccessor actor)
{
    public async Task<Result<TaxEstimateView>> EstimateAsync(int year, VisibilityScope scope, NodaTime.LocalDate today, CancellationToken ct)
    {
        var read = await ReadAsync(year, scope, today, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<TaxEstimateView>(failure!);
        read.TryGetValue(out var r);
        var (lines, stored, people) = r;
        var profile = stored?.Profile ?? TaxProfile.Empty;
        var (inputs, notices, _) = Assemble(year, lines, profile, people);
        var adults = Adults(people);
        return Result.Success(new TaxEstimateView(
            year, TaxProfileView.Of(profile), stored is { } s && s.Year != year ? s.Year : null,
            TaxInputsView.Of(inputs), TaxResultView.Of(TaxCalculator.Compute(inputs)), notices,
            adults.Select(a => new TaxAdultView(a.Id.Value, a.DisplayName)).ToList(),
            people.Count(p => p.Kind == PersonKind.Dependent),
            lines.Where(l => l.Code == TaxLineCodes.RentalProperty).Select(l => new TaxPropertyView(l.Item, l.Name, l.On!.Value.Year)).ToList()));
    }

    /// <summary>
    /// Rental 2044 spec §4–§5: the form from the estimate's own lines and calculation, so the two never differ; the regime is the
    /// estimate's, the totals <see cref="Rental2044.Totals"/>' and the 2042 boxes theirs (4BE alone under the micro-foncier).
    /// </summary>
    public async Task<Result<Rental2044View>> Rental2044Async(int year, VisibilityScope scope, NodaTime.LocalDate today, CancellationToken ct)
    {
        var read = await ReadAsync(year, scope, today, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<Rental2044View>(failure!);
        read.TryGetValue(out var r);
        var (lines, stored, people) = r;
        var (inputs, notices, columns) = Assemble(year, lines, stored?.Profile ?? TaxProfile.Empty, people);
        var regime = TaxCalculator.Compute(inputs).RentalRegime;
        var t = Rental2044.Totals(columns, TaxRules.For(year).RentalDeficitCap);
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

    /// <summary>Every module's lines for the year, the year's profile and the household.</summary>
    private async Task<Result<(IReadOnlyList<TaxLine> Lines, StoredTaxProfile? Stored, IReadOnlyList<PersonInfo> People)>> ReadAsync(
        int year, VisibilityScope scope, NodaTime.LocalDate today, CancellationToken ct)
    {
        var reader = actor.Current!.PersonId!.Value;
        var collected = await Contributions.CollectAsync(contributions, c => c.TaxLinesAsync(reader, scope, year, today, ct));
        if (collected.TryGetFailure(out var failure))
            return Result.Failure<(IReadOnlyList<TaxLine>, StoredTaxProfile?, IReadOnlyList<PersonInfo>)>(failure!);
        collected.TryGetValue(out var parts);
        return Result.Success<(IReadOnlyList<TaxLine>, StoredTaxProfile?, IReadOnlyList<PersonInfo>)>(
            (parts!.SelectMany(p => p).ToList(), await store.FindAsync(year, ct), await persons.ListAsync(ct)));
    }

    /// <summary>The adults who declare, in a stable order: the household's first two.</summary>
    public static IReadOnlyList<PersonInfo> Adults(IReadOnlyList<PersonInfo> people)
        => people.Where(p => p.Kind == PersonKind.Adult).OrderBy(p => p.DisplayName, StringComparer.Ordinal).ThenBy(p => p.Id.Value).Take(2).ToList();

    /// <summary>Rental 2044 spec §4: the estimate's rental figures are the form's columns, also returned for the form.</summary>
    public static (TaxInputs Inputs, IReadOnlyList<TaxNoticeView> Notices, IReadOnlyList<Rental2044Column> Columns) Assemble(
        int year, IReadOnlyList<TaxLine> lines, TaxProfile profile, IReadOnlyList<PersonInfo> people)
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
            var declarant = new TaxDeclarant(adult.DisplayName, 0m, status, Sum(TaxLineCodes.OtherTaxable), Sum(TaxLineCodes.PerContribution), rate, expenses);
            var gross = Sum(TaxLineCodes.SalaryGross) + (Sum(TaxLineCodes.SalaryNet) + Sum(TaxLineCodes.SalaryOneOff)) / (1 - declarant.EffectiveContributionRate);
            declarants.Add(declarant with { SalaryGross = gross });
        }
        if (declarants.Count == 0)
            declarants.Add(new TaxDeclarant("", 0m, EmploymentStatus.NonExecutive, 0m, 0m));

        string[] personal = [TaxLineCodes.SalaryGross, TaxLineCodes.SalaryNet, TaxLineCodes.SalaryOneOff, TaxLineCodes.OtherTaxable, TaxLineCodes.PerContribution];
        foreach (var line in lines)
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
            profile.ChildcareOutsideHome, profile.HomeEmployment, profile.Donations, profile.DonationsAid);
        return (inputs, notices, columns);
    }

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
                 && new[] { p.ChildcareOutsideHome, p.HomeEmployment, p.Donations, p.DonationsAid }.All(a => a is >= 0 and <= 10_000_000m)
                 && (p.ContributionRates?.Values.All(RateOk) ?? true) && (p.ActualExpenses?.Values.All(a => a is >= 0 and <= 10_000_000m) ?? true);
        return ok ? null : new Failure(TaxCodes.InvalidProfile,
            "Statuses are non_executive, executive or public; a contribution rate is between 0 and 60 %; the regime micro or real; a Pinel commitment starts at 6 or 9 years and lasts 6, 9 or 12, once per property; amounts are not negative.", null);
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
