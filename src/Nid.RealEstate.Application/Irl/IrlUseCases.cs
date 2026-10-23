using System.Globalization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.RealEstate.Application.Irl;

/// <summary>Sent by the <c>irl-weekly</c> job as the system actor, who is admin.</summary>
public sealed record CollectIrlCommand : ICommand<IrlRunView>, IAdminOnly;

public sealed record RequestIrlSyncCommand : ICommand<IrlSyncRequested>, IAdminOnly;

public sealed record GetIrlQuery : IQuery<IrlAdminView>, IAdminOnly;

/// <summary>
/// A value INSEE has published but Nid has not read yet, typed in by the admin; <c>PublishedOn</c> is the date printed in the
/// Journal officiel, which decides the anniversaries it serves.
/// </summary>
public sealed record SetIrlValueCommand(int Year, int Quarter, decimal Value, LocalDate PublishedOn) : ICommand<IrlValueView>, IAdminOnly;

public static class IrlViews
{
    public static IrlValueView From(IrlValue v)
        => new(v.Quarter.ToString(), v.Value.ToString("0.00##", CultureInfo.InvariantCulture), v.PublishedOn, v.FetchedOn, v.Source);
}

[RequestHandler<CollectIrlCommand, IrlRunView>]
public sealed class CollectIrlHandler(IrlCollector collector) : IRequestHandler<CollectIrlCommand, IrlRunView>
{
    public async ValueTask<Result<IrlRunView>> HandleAsync(CollectIrlCommand command, CancellationToken ct = default)
        => await collector.CollectAsync(ct);
}

[RequestHandler<RequestIrlSyncCommand, IrlSyncRequested>]
public sealed class RequestIrlSyncHandler(IIrlRunLog runs, IHouseholdSettings settings, IIrlJobTrigger trigger)
    : IRequestHandler<RequestIrlSyncCommand, IrlSyncRequested>
{
    public async ValueTask<Result<IrlSyncRequested>> HandleAsync(RequestIrlSyncCommand command, CancellationToken ct = default)
    {
        if (await runs.IsRunningAsync(ct))
            return Result.Failure<IrlSyncRequested>(new Failure(IrlCodes.SyncRunning, "An IRL collection is already running.", null));
        if (!(await settings.ReadAsync(ct)).IrlEnabled)
            return Result.Failure<IrlSyncRequested>(new Failure(IrlCodes.Disabled, "IRL collection is turned off for this household.", null));

        var triggered = await trigger.TriggerAsync(ct);
        return triggered.TryGetFailure(out var failure)
            ? Result.Failure<IrlSyncRequested>(failure!)
            : Result.Success(new IrlSyncRequested(true));
    }
}

[RequestHandler<GetIrlQuery, IrlAdminView>]
public sealed class GetIrlHandler(IIrlStore store, IIrlRunLog runs) : IRequestHandler<GetIrlQuery, IrlAdminView>
{
    public const int RecentRuns = 50;

    public async ValueTask<Result<IrlAdminView>> HandleAsync(GetIrlQuery query, CancellationToken ct = default)
    {
        var values = (await store.ListAsync(ct)).OrderByDescending(v => v.Quarter.Year).ThenByDescending(v => v.Quarter.Quarter).Select(IrlViews.From).ToList();
        return Result.Success(new IrlAdminView(values, await runs.RecentAsync(RecentRuns, ct)));
    }
}

[RequestHandler<SetIrlValueCommand, IrlValueView>]
public sealed class SetIrlValueHandler(IIrlStore store, IHouseholdCalendar calendar) : IRequestHandler<SetIrlValueCommand, IrlValueView>
{
    public async ValueTask<Result<IrlValueView>> HandleAsync(SetIrlValueCommand command, CancellationToken ct = default)
    {
        var today = await calendar.TodayAsync(ct);
        if (command.Quarter is < 1 or > 4 || command.Year < 2000 || command.Year > today.Year)
            return Refused("quarter", $"The quarter is 1 to 4 of a year from 2000 to {today.Year}.");
        var quarter = new IrlQuarter(command.Year, command.Quarter);
        var over = new LocalDate(quarter.Year, 3 * quarter.Quarter, 1).PlusMonths(1);
        if (today < over)
            return Refused("quarter", $"{quarter} is not over yet.");
        if (command.PublishedOn < over || command.PublishedOn > today)
            return Refused("publishedOn", $"The publication date lies between the end of {quarter} and today.");
        var series = await store.ListAsync(ct);
        var lastYear = series.FirstOrDefault(v => v.Quarter == quarter with { Year = quarter.Year - 1 })?.Value;
        if (command.Value <= 0m || command.Value != Math.Round(command.Value, 2) || !IrlPlausibility.Plausible(command.Value, lastYear))
            return Refused("value", "The value is above zero, with two decimals at most, within −5 % and +10 % of the same quarter a year earlier.");

        if (series.Any(v => v.Quarter == quarter && v.Source == IrlCollector.Insee))
            return Result.Failure<IrlValueView>(new Failure(IrlCodes.IrlFromInsee, $"INSEE's value for {quarter} is already stored and stays.", null));

        var stored = new IrlValue(quarter, command.Value, command.PublishedOn, today, IrlCollector.Manual);
        // The conditional write closes the race with a collection storing INSEE's value between the check and here.
        return await store.UpsertManualAsync(stored, ct)
            ? Result.Success(IrlViews.From(stored))
            : Result.Failure<IrlValueView>(new Failure(IrlCodes.IrlFromInsee, $"INSEE's value for {quarter} is already stored and stays.", null));
    }

    private static Result<IrlValueView> Refused(string field, string message)
        => Result.Failure<IrlValueView>(new Failure(IrlCodes.InvalidIrlValue, message, new Dictionary<string, object?> { ["field"] = field }));
}
