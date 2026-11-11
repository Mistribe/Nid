using System.Globalization;
using Nid.Core.Application.Inflation;
using Nid.Core.Contracts;
using Nid.Core.Domain.Projection;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Projection;

public static class ProjectionCodes
{
    public const string InvalidField = "projection.invalid_field";

    /// <summary>Spec §4.5: an event names a person, loan, property or purchase scenario the writer does not know or cannot see.</summary>
    public const string TargetNotFound = "projection.target_not_found";

    /// <summary>Spec §2.2: a retirement horizon names a person with no birth date, or no person the reader knows.</summary>
    public const string NoBirthDate = "projection.no_birth_date";

    /// <summary>Spec §2.3: a retirement already reached, or more than 40 years away.</summary>
    public const string InvalidHorizon = "projection.invalid_horizon";
}

/// <summary>Net worth projection spec §2.1: the limits of the household's assumptions. Fields are the camelCase wire paths.</summary>
public static class ProjectionRules
{
    public const decimal MaxRate = 0.5m;
    public const decimal MinInflation = -0.05m;
    public const decimal MaxInflation = 0.2m;
    public const decimal MinIncomeGrowth = -0.05m;
    public const decimal MaxIncomeGrowth = 0.1m;

    /// <summary>Ranges spec §2: a class's annual volatility, from 0 to 50 %.</summary>
    public const decimal MaxVolatility = 0.5m;

    /// <summary>Retirement spec §3: the withdrawal rate, above 0 and at most 10 %.</summary>
    public const decimal MaxWithdrawalRate = 0.1m;

    /// <summary>Inflation spec C15: the years a yearly rate may be set for, from the current year on.</summary>
    public const int YearsAhead = 4;

    /// <summary>Inflation spec C15: the classes with yearly rates.</summary>
    public static readonly IReadOnlyList<string> YearlyClasses = [AllocationClasses.Cash, AllocationClasses.MoneyMarket];

    /// <summary>Every refusal; empty when valid. A yearly rate is for a calendar year from <paramref name="today"/>'s to 4 years later.</summary>
    public static IReadOnlyList<(string Field, string Message)> Check(ProjectionAssumptions a, LocalDate today)
    {
        var errors = new List<(string, string)>();
        foreach (var @class in AllocationClasses.All.Where(c => !a.ClassRates.ContainsKey(c)))
            errors.Add(($"classRates.{@class}", "A rate is required for every asset class."));
        foreach (var (@class, rate) in a.ClassRates)
        {
            if (!AllocationClasses.All.Contains(@class))
                errors.Add(($"classRates.{@class}", $"Not an asset class: use {string.Join(", ", AllocationClasses.All)}."));
            else if (Math.Abs(rate) > MaxRate)
                errors.Add(($"classRates.{@class}", "A rate is from -50 % to 50 %."));
        }
        if (a.Inflation is < MinInflation or > MaxInflation) errors.Add(("inflation", "Inflation is from -5 % to 20 %."));
        if (!AllocationClasses.All.Contains(a.SavingsClass))
            errors.Add(("savingsClass", $"Not an asset class: use {string.Join(", ", AllocationClasses.All)}."));

        var years = $"A year is from {today.Year} to {today.Year + YearsAhead}.";
        bool Outside(int year) => year < today.Year || year > today.Year + YearsAhead;
        foreach (var (year, rate) in a.InflationByYear ?? new Dictionary<int, decimal>())
        {
            if (Outside(year)) errors.Add(($"inflationByYear.{year}", years));
            else if (rate is < MinInflation or > MaxInflation) errors.Add(($"inflationByYear.{year}", "Inflation is from -5 % to 20 %."));
        }
        foreach (var (@class, table) in a.ClassRatesByYear ?? new Dictionary<string, IReadOnlyDictionary<int, decimal>>())
        {
            if (table is null) continue; // `{"cash": null}`: no yearly rate for the class
            if (!YearlyClasses.Contains(@class))
            {
                errors.Add(($"classRatesByYear.{@class}", $"Yearly rates are for {string.Join(", ", YearlyClasses)} only."));
                continue;
            }
            foreach (var (year, rate) in table)
            {
                if (Outside(year)) errors.Add(($"classRatesByYear.{@class}.{year}", years));
                else if (Math.Abs(rate) > MaxRate) errors.Add(($"classRatesByYear.{@class}.{year}", "A rate is from -50 % to 50 %."));
            }
        }
        if (a.IncomeGrowth is < MinIncomeGrowth or > MaxIncomeGrowth)
            errors.Add(("incomeGrowth", "Income growth over inflation is from -5 % to 10 %."));
        foreach (var (@class, sigma) in a.ClassVolatility ?? new Dictionary<string, decimal>())
        {
            if (!AllocationClasses.All.Contains(@class))
                errors.Add(($"classVolatility.{@class}", $"Not an asset class: use {string.Join(", ", AllocationClasses.All)}."));
            else if (sigma is < 0m or > MaxVolatility)
                errors.Add(($"classVolatility.{@class}", "A volatility is from 0 % to 50 %."));
        }
        if (a.WithdrawalRate is <= 0m or > MaxWithdrawalRate)
            errors.Add(("withdrawalRate", "The withdrawal rate is above 0 % and at most 10 %."));
        if (a.MonthlyLivingCost < 0m)
            errors.Add(("monthlyLivingCost", "The living cost is not negative."));
        return errors;
    }

    /// <summary>
    /// Inflation spec C15: the assumptions without the yearly rates of years before <paramref name="today"/>'s, dropped at a save. A
    /// class left with no year is dropped, and a table left empty is null, as one never set.
    /// </summary>
    public static ProjectionAssumptions WithoutPastYears(ProjectionAssumptions a, LocalDate today)
    {
        IReadOnlyDictionary<int, decimal>? Kept(IReadOnlyDictionary<int, decimal>? table)
            => table?.Where(y => y.Key >= today.Year).ToDictionary(y => y.Key, y => y.Value) is { Count: > 0 } kept ? kept : null;
        var classes = (a.ClassRatesByYear ?? new Dictionary<string, IReadOnlyDictionary<int, decimal>>())
            .Select(c => (c.Key, Table: Kept(c.Value))).Where(c => c.Table is not null).ToDictionary(c => c.Key, c => c.Table!);
        return a with { InflationByYear = Kept(a.InflationByYear), ClassRatesByYear = classes.Count > 0 ? classes : null };
    }

    /// <summary>
    /// The observed inflation shown beside the current year: only before December and when that year has no typed rate, the rule the
    /// projection follows.
    /// </summary>
    public static bool ShowsObserved(ProjectionAssumptions a, LocalDate today)
        => today.Month < 12 && a.InflationByYear?.ContainsKey(today.Year) != true;

    /// <summary>A scenario's overrides without their past years, as <see cref="WithoutPastYears(ProjectionAssumptions, LocalDate)"/>.</summary>
    public static ProjectionOverrides WithoutPastYears(ProjectionOverrides o, LocalDate today)
    {
        var kept = WithoutPastYears(new ProjectionAssumptions(
            new Dictionary<string, decimal>(), 0m, null, "", false, o.InflationByYear, o.ClassRatesByYear), today);
        return o with { InflationByYear = kept.InflationByYear, ClassRatesByYear = kept.ClassRatesByYear };
    }
}

/// <summary>
/// Percent strings for the rates (<c>"6"</c>), the savings in the reference currency (<c>"500.00"</c>, <c>null</c>: automatic); <c>Currency</c> is the
/// household reference currency code the savings are in. Inflation spec C15: the yearly tables are keyed by calendar year (<c>"2026"</c>);
/// <c>ObservedInflation</c> is the rate the current year follows when it has no value, for the form's hint (null elsewhere than in the settings).
/// Ranges spec §2: <c>ClassVolatility</c> holds every class's annual volatility in percent, the defaults included.
/// Retirement spec §3: <c>WithdrawalRate</c> in percent, <c>MonthlyLivingCost</c> an amount in the reference currency.
/// #155: <c>Profile</c> is the market profile the rates match (<see cref="ProjectionProfiles"/>), <c>custom</c> otherwise;
/// <c>Defaults</c> the value each customised field resets to.
/// </summary>
public sealed record ProjectionAssumptionsView(
    IReadOnlyDictionary<string, string> ClassRates, string Inflation, string? MonthlySavings, string SavingsClass, bool FreedPaymentsToSavings, string Currency,
    IReadOnlyDictionary<string, string> InflationByYear, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ClassRatesByYear, string IncomeGrowth,
    IReadOnlyDictionary<string, string> ClassVolatility, string WithdrawalRate, string MonthlyLivingCost, string Profile, ProjectionDefaultsView Defaults,
    InflationTwelveMonthsView? ObservedInflation = null)
{
    public static ProjectionAssumptionsView From(ProjectionAssumptions a, string currency, InflationTwelveMonthsView? observed = null) => new(
        a.ClassRates.ToDictionary(r => r.Key, r => Percent(r.Value)), Percent(a.Inflation),
        a.MonthlySavings is { } savings ? Scenarios.PropertyPurchaseView.Amount(savings) : null, a.SavingsClass, a.FreedPaymentsToSavings, currency,
        Years(a.InflationByYear) ?? new Dictionary<string, string>(),
        (a.ClassRatesByYear ?? new Dictionary<string, IReadOnlyDictionary<int, decimal>>()).Where(c => c.Value is not null)
            .ToDictionary(c => c.Key, c => (IReadOnlyDictionary<string, string>)Years(c.Value)!),
        Percent(a.IncomeGrowth), AllocationClasses.All.ToDictionary(c => c, c => Percent(a.VolatilityOf(c))),
        Percent(a.WithdrawalRate), Scenarios.PropertyPurchaseView.Amount(a.MonthlyLivingCost), ProjectionProfiles.Of(a), ProjectionDefaultsView.Instance, observed);

    /// <summary>A class table in percent strings.</summary>
    internal static Dictionary<string, string> Percents(IReadOnlyDictionary<string, decimal> table) => table.ToDictionary(r => r.Key, r => Percent(r.Value));

    /// <summary>A yearly table on the wire, years as text and rates in percent; null for a null table.</summary>
    public static Dictionary<string, string>? Years(IReadOnlyDictionary<int, decimal>? table)
        => table?.OrderBy(y => y.Key).ToDictionary(y => y.Key.ToString(CultureInfo.InvariantCulture), y => Percent(y.Value));

    /// <summary>
    /// A yearly table from the wire: a key that is not a year is refused at <c>{path}.{key}</c>; a rate is read by
    /// <paramref name="percent"/> (which refuses its own text) and divided by 100. Null for a null table.
    /// </summary>
    public static Dictionary<int, decimal>? Years(
        IReadOnlyDictionary<string, string>? wire, string path, Func<string?, string, decimal?> percent, Action<string, string> refuse)
    {
        if (wire is null) return null;
        var table = new Dictionary<int, decimal>();
        foreach (var (key, text) in wire)
        {
            var at = $"{path}.{key}";
            // The canonical text only: "02026" would silently overwrite "2026". A bad key is refused alone, its value unread.
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year.ToString(CultureInfo.InvariantCulture) != key)
                refuse(at, "A year is a number such as \"2026\".");
            else if (percent(text, at) is { } value) table[year] = value / 100m;
        }
        return table;
    }

    /// <summary>A class's yearly tables from the wire, as <see cref="Years(IReadOnlyDictionary{string, string}?, string, Func{string?, string, decimal?}, Action{string, string})"/>; a null inner table is absent.</summary>
    public static Dictionary<string, IReadOnlyDictionary<int, decimal>>? ClassYears(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? wire, string path, Func<string?, string, decimal?> percent, Action<string, string> refuse)
    {
        if (wire is null) return null;
        var tables = new Dictionary<string, IReadOnlyDictionary<int, decimal>>();
        foreach (var (@class, table) in wire)
            if (Years(table, $"{path}.{@class}", percent, refuse) is { } years) tables[@class] = years;
            else tables[@class] = null!; // `{"cash": null}`: absent, as the rules and Apply read it
        return tables;
    }

    internal static string Percent(decimal fraction) => Scenarios.PropertyPurchaseView.Rate(fraction);
}

/// <summary>#155: the default of every value a customised field resets to, in percent: the balanced profile's rates and volatilities.</summary>
public sealed record ProjectionDefaultsView(
    IReadOnlyDictionary<string, string> ClassRates, IReadOnlyDictionary<string, string> ClassVolatility, string Inflation, string IncomeGrowth, string WithdrawalRate)
{
    public static readonly ProjectionDefaultsView Instance = new(
        ProjectionAssumptionsView.Percents(ProjectionDefaults.ClassRates), ProjectionAssumptionsView.Percents(ProjectionDefaults.ClassVolatility),
        ProjectionAssumptionsView.Percent(ProjectionDefaults.Assumptions.Inflation), ProjectionAssumptionsView.Percent(ProjectionDefaults.Assumptions.IncomeGrowth),
        ProjectionAssumptionsView.Percent(ProjectionDefaults.Assumptions.WithdrawalRate));
}

public sealed record GetProjectionAssumptionsQuery : IQuery<ProjectionAssumptionsView>;

/// <summary>Spec §2.1: replaces the household's assumptions whole; any adult may.</summary>
public sealed record SaveProjectionAssumptionsCommand(ProjectionAssumptions Assumptions) : ICommand<ProjectionAssumptionsView>;

[RequestHandler<GetProjectionAssumptionsQuery, ProjectionAssumptionsView>]
public sealed class GetProjectionAssumptionsHandler(IHouseholdSettings household, IHouseholdCalendar calendar, IInflation inflation) : IRequestHandler<GetProjectionAssumptionsQuery, ProjectionAssumptionsView>
{
    public async ValueTask<Result<ProjectionAssumptionsView>> HandleAsync(GetProjectionAssumptionsQuery query, CancellationToken ct = default)
    {
        // C15: a stored past year is ignored when read.
        var today = await calendar.TodayAsync(ct);
        var stored = ProjectionRules.WithoutPastYears(await household.ProjectionAssumptionsAsync(ct), today);
        return Result.Success(await ProjectionAssumptionsViews.ReadAsync(household, inflation, stored, today, ct));
    }
}

internal static class ProjectionAssumptionsViews
{
    /// <summary>The view with the observed inflation hint, by the rule of <see cref="ProjectionFactsReader"/>: EUR households only.</summary>
    public static async Task<ProjectionAssumptionsView> ReadAsync(IHouseholdSettings household, IInflation inflation, ProjectionAssumptions a, LocalDate today, CancellationToken ct)
    {
        var reference = await household.ReferenceCurrencyAsync(ct);
        var last = reference == Currency.Eur && ProjectionRules.ShowsObserved(a, today) ? (await inflation.ReadAsync(ct)).Last12Months : null;
        return ProjectionAssumptionsView.From(a, reference.Code, last is { } l ? new InflationTwelveMonthsView(CpiViews.Month(l.To), CpiViews.Percent(l.Rate)) : null);
    }

    /// <summary>
    /// Stores what <paramref name="change"/> makes of the stored assumptions, without their past years (a past year sent back is dropped,
    /// never refused), once <see cref="ProjectionRules.Check"/> passes, and answers the view; every refusal keeps its field path. The read,
    /// the change and the write hold one lock (<see cref="IHouseholdSettings.UpdateProjectionAssumptionsAsync"/>).
    /// </summary>
    public static async Task<Result<ProjectionAssumptionsView>> SaveAsync(
        IHouseholdSettings household, IInflation inflation, Func<ProjectionAssumptions, ProjectionAssumptions> change, LocalDate today, CancellationToken ct)
    {
        var saved = await household.UpdateProjectionAssumptionsAsync(stored =>
        {
            var assumptions = ProjectionRules.WithoutPastYears(change(stored), today);
            var errors = ProjectionRules.Check(assumptions, today);
            return errors.Count > 0
                ? Result.Failure<ProjectionAssumptions>(FieldErrors.Many(ProjectionCodes.InvalidField,
                    errors.Count == 1 ? errors[0].Message : "Some fields are not valid.",
                    errors.Select(e => new FieldError(e.Field, ProjectionCodes.InvalidField, e.Message)).ToList()))
                : Result.Success(assumptions);
        }, ct);
        if (saved.TryGetFailure(out var failure)) return Result.Failure<ProjectionAssumptionsView>(failure!);
        saved.TryGetValue(out var stored);
        return Result.Success(await ReadAsync(household, inflation, stored!, today, ct));
    }
}

[RequestHandler<SaveProjectionAssumptionsCommand, ProjectionAssumptionsView>]
public sealed class SaveProjectionAssumptionsHandler(IHouseholdSettings household, IHouseholdCalendar calendar, IInflation inflation)
    : IRequestHandler<SaveProjectionAssumptionsCommand, ProjectionAssumptionsView>
{
    public async ValueTask<Result<ProjectionAssumptionsView>> HandleAsync(SaveProjectionAssumptionsCommand command, CancellationToken ct = default)
        => await ProjectionAssumptionsViews.SaveAsync(household, inflation, _ => command.Assumptions, await calendar.TodayAsync(ct), ct);
}

/// <summary>
/// #155: changes part of the household's assumptions, a null member of <c>Changes</c> keeping the stored value (the merge rules of
/// <see cref="ProjectionOverrides.Apply"/>). <c>Profile</c> first sets every class rate and volatility to the profile's, so it comes
/// without rates or volatilities of its own; <c>AutomaticSavings</c> goes back to the automatic savings. Any adult may.
/// </summary>
public sealed record PatchProjectionAssumptionsCommand(string? Profile, ProjectionOverrides Changes, bool AutomaticSavings = false)
    : ICommand<ProjectionAssumptionsView>;

[RequestHandler<PatchProjectionAssumptionsCommand, ProjectionAssumptionsView>]
public sealed class PatchProjectionAssumptionsHandler(IHouseholdSettings household, IHouseholdCalendar calendar, IInflation inflation)
    : IRequestHandler<PatchProjectionAssumptionsCommand, ProjectionAssumptionsView>
{
    public async ValueTask<Result<ProjectionAssumptionsView>> HandleAsync(PatchProjectionAssumptionsCommand command, CancellationToken ct = default)
    {
        var refusal = command.Profile is not { } profile ? null
            : !ProjectionProfiles.All.Contains(profile) ? $"Not a profile: use {string.Join(", ", ProjectionProfiles.All)}."
            : command.Changes.ClassRates is not null || command.Changes.ClassVolatility is not null
                ? "A profile sets every class rate and volatility: send it without classRates or classVolatility."
                : null;
        if (refusal is not null)
            return Result.Failure<ProjectionAssumptionsView>(FieldErrors.Many(ProjectionCodes.InvalidField, refusal,
                [new FieldError("profile", ProjectionCodes.InvalidField, refusal)]));

        return await ProjectionAssumptionsViews.SaveAsync(household, inflation, stored =>
        {
            if (command.Profile is not null) stored = ProjectionProfiles.Apply(command.Profile, stored);
            var merged = command.Changes.Apply(stored);
            return command.AutomaticSavings ? merged with { MonthlySavings = null } : merged;
        }, await calendar.TodayAsync(ct), ct);
    }
}

/// <summary>
/// #155: a market profile's rates and volatilities in percent, and <c>InTenYears</c>: the household's base projection net worth at 10
/// years in today's money, with its other assumptions and the profile's rates.
/// </summary>
public sealed record ProjectionProfileView(string Key, IReadOnlyDictionary<string, string> ClassRates, IReadOnlyDictionary<string, string> ClassVolatility, string InTenYears);

/// <summary>#155: every market profile, its amounts in the household reference currency.</summary>
public sealed record ProjectionProfilesView(string Currency, IReadOnlyList<ProjectionProfileView> Profiles);

/// <summary>#155: the market profiles and their effect, in the household scope: three deterministic runs, no bands.</summary>
public sealed record GetProjectionProfilesQuery : IQuery<ProjectionProfilesView>;

[RequestHandler<GetProjectionProfilesQuery, ProjectionProfilesView>]
public sealed class GetProjectionProfilesHandler(ProjectionFactsReader facts, IHouseholdSettings household)
    : IRequestHandler<GetProjectionProfilesQuery, ProjectionProfilesView>
{
    public async ValueTask<Result<ProjectionProfilesView>> HandleAsync(GetProjectionProfilesQuery query, CancellationToken ct = default)
    {
        var stored = await household.ProjectionAssumptionsAsync(ct);
        var profiles = new List<ProjectionProfileView>();
        var currency = (await household.ReferenceCurrencyAsync(ct)).Code;
        foreach (var profile in ProjectionProfiles.All)
        {
            var assumptions = ProjectionProfiles.Apply(profile, stored);
            var read = await facts.ReadAsync(VisibilityScope.Household, ProjectionHorizon.Default, assumptions, RealEstateEvents.None, null, null, ct);
            if (read.TryGetFailure(out var failure)) return Result.Failure<ProjectionProfilesView>(failure!);
            read.TryGetValue(out var reading);
            var end = HouseholdProjection.Project(reading!.Inputs).Points[^1];
            profiles.Add(new ProjectionProfileView(profile, ProjectionAssumptionsView.Percents(assumptions.ClassRates),
                AllocationClasses.All.ToDictionary(c => c, c => ProjectionAssumptionsView.Percent(assumptions.VolatilityOf(c))),
                Scenarios.PropertyPurchaseView.Amount(end.Real)));
        }
        return Result.Success(new ProjectionProfilesView(currency, profiles));
    }
}
