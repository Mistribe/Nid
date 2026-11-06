using System.Globalization;
using Nid.Core.Contracts;
using Nid.Core.Domain.Inflation;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Inflation;

/// <summary>Sent by the <c>cpi-weekly</c> job as the system actor, who is admin.</summary>
public sealed record CollectCpiCommand : ICommand<CpiRunView>, IAdminOnly;

public sealed record RequestCpiSyncCommand : ICommand<CpiSyncRequested>, IAdminOnly;

public sealed record GetCpiQuery : IQuery<CpiAdminView>, IAdminOnly;

/// <summary>A month INSEE has published but Nid has not read yet, typed in by the admin; INSEE's value replaces it on the next collection.</summary>
public sealed record SetCpiValueCommand(int Year, int Month, decimal Value) : ICommand<CpiValueView>, IAdminOnly;

/// <summary>What every household member sees: the last twelve months and the last complete years, as percents with one decimal.</summary>
public sealed record GetInflationQuery : IQuery<InflationView>;

public sealed record InflationView(string? LastPublished, InflationTwelveMonthsView? Last12Months, IReadOnlyList<InflationYearView> Years);

public sealed record InflationTwelveMonthsView(string To, string Percent);

public sealed record InflationYearView(int Year, string Percent);

public static class CpiViews
{
    public static CpiValueView From(CpiValue v)
        => new(Month(v.Month), v.Value.ToString("0.00##", CultureInfo.InvariantCulture), v.PublishedOn, v.FetchedOn, v.Source);

    /// <summary>The month as <c>2026-09</c>, on the wire and in messages.</summary>
    internal static string Month(YearMonth month) => YearMonthPattern.Iso.Format(month);

    public static string Percent(decimal fraction) => Math.Round(fraction * 100m, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture);
}

/// <summary>The stored CPI as the shared <see cref="InflationSeries"/>; empty before the first collection.</summary>
public sealed class ObservedInflation(ICpiStore store) : IInflation
{
    public async Task<InflationSeries> ReadAsync(CancellationToken ct)
        => new((await store.ListAsync(ct)).ToDictionary(v => v.Month, v => v.Value));
}

[RequestHandler<CollectCpiCommand, CpiRunView>]
public sealed class CollectCpiHandler(CpiCollector collector) : IRequestHandler<CollectCpiCommand, CpiRunView>
{
    public async ValueTask<Result<CpiRunView>> HandleAsync(CollectCpiCommand command, CancellationToken ct = default)
        => await collector.CollectAsync(ct);
}

[RequestHandler<RequestCpiSyncCommand, CpiSyncRequested>]
public sealed class RequestCpiSyncHandler(ICpiRunLog runs, IHouseholdSettings settings, ICpiJobTrigger trigger)
    : IRequestHandler<RequestCpiSyncCommand, CpiSyncRequested>
{
    public async ValueTask<Result<CpiSyncRequested>> HandleAsync(RequestCpiSyncCommand command, CancellationToken ct = default)
    {
        if (await runs.IsRunningAsync(ct))
            return Result.Failure<CpiSyncRequested>(new Failure(InflationCodes.SyncRunning, "A CPI collection is already running.", null));
        if (!(await settings.ReadAsync(ct)).InflationEnabled)
            return Result.Failure<CpiSyncRequested>(new Failure(InflationCodes.Disabled, "CPI collection is turned off for this household.", null));

        var triggered = await trigger.TriggerAsync(ct);
        return triggered.TryGetFailure(out var failure)
            ? Result.Failure<CpiSyncRequested>(failure!)
            : Result.Success(new CpiSyncRequested(true));
    }
}

[RequestHandler<GetCpiQuery, CpiAdminView>]
public sealed class GetCpiHandler(ICpiStore store, ICpiRunLog runs) : IRequestHandler<GetCpiQuery, CpiAdminView>
{
    public const int RecentRuns = 50;

    public async ValueTask<Result<CpiAdminView>> HandleAsync(GetCpiQuery query, CancellationToken ct = default)
    {
        var values = (await store.ListAsync(ct)).OrderByDescending(v => v.Month).Select(CpiViews.From).ToList();
        return Result.Success(new CpiAdminView(values, await runs.RecentAsync(RecentRuns, ct)));
    }
}

[RequestHandler<SetCpiValueCommand, CpiValueView>]
public sealed class SetCpiValueHandler(ICpiStore store, IHouseholdCalendar calendar) : IRequestHandler<SetCpiValueCommand, CpiValueView>
{
    public async ValueTask<Result<CpiValueView>> HandleAsync(SetCpiValueCommand command, CancellationToken ct = default)
    {
        var today = await calendar.TodayAsync(ct);
        if (command.Month is < 1 or > 12 || command.Year < 1996 || command.Year > today.Year)
            return Refused("month", $"The month is 1 to 12 of a year from 1996 to {today.Year}.");
        var month = new YearMonth(command.Year, command.Month);
        if (today < new LocalDate(month.Year, month.Month, 1).PlusMonths(1))
            return Refused("month", $"{CpiViews.Month(month)} is not over yet.");
        var series = await store.ListAsync(ct);
        var before = series.FirstOrDefault(v => v.Month == month.PlusMonths(-1))?.Value;
        if (command.Value <= 0m || command.Value >= 1_000_000m || command.Value != Math.Round(command.Value, 4) || !CpiPlausibility.Plausible(command.Value, before))
            return Refused("value", "The value is above zero and under 1,000,000, with four decimals at most, within 3 % of the month before.");

        if (series.Any(v => v.Month == month && v.Source == CpiCollector.Insee))
            return FromInsee(month);

        var stored = new CpiValue(month, command.Value, null, today, CpiCollector.Manual);
        // The conditional write closes the race with a collection storing INSEE's value between the check and here.
        return await store.UpsertManualAsync(stored, ct) ? Result.Success(CpiViews.From(stored)) : FromInsee(month);
    }

    private static Result<CpiValueView> FromInsee(YearMonth month)
        => Result.Failure<CpiValueView>(new Failure(InflationCodes.FromInsee, $"INSEE's value for {CpiViews.Month(month)} is already stored and stays.", null));

    private static Result<CpiValueView> Refused(string field, string message)
        => Result.Failure<CpiValueView>(new Failure(InflationCodes.InvalidValue, message, new Dictionary<string, object?> { ["field"] = field }));
}

[RequestHandler<GetInflationQuery, InflationView>]
public sealed class GetInflationHandler(IInflation inflation) : IRequestHandler<GetInflationQuery, InflationView>
{
    public const int Years = 10;

    public async ValueTask<Result<InflationView>> HandleAsync(GetInflationQuery query, CancellationToken ct = default)
    {
        var series = await inflation.ReadAsync(ct);
        if (series.LastPublished is not { } last)
            return Result.Success(new InflationView(null, null, []));
        var years = Enumerable.Range(1997, last.Year - 1997 + 1).Reverse()
            .Select(y => (Year: y, Rate: series.Annual(y))).Where(y => y.Rate is not null)
            .Take(Years).Select(y => new InflationYearView(y.Year, CpiViews.Percent(y.Rate!.Value))).ToList();
        var twelve = series.Last12Months is { } t ? new InflationTwelveMonthsView(CpiViews.Month(t.To), CpiViews.Percent(t.Rate)) : null;
        return Result.Success(new InflationView(CpiViews.Month(last), twelve, years));
    }
}
