using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Application;

/// <summary>Fetches the raw SDMX document of the INSEE series through the outbound client; never throws, a failure is an <see cref="IrlCodes"/> code.</summary>
public interface IIrlSourceClient
{
    Task<Result<byte[]>> FetchAsync(CancellationToken ct);
}

/// <summary>Starts the <c>irl-weekly</c> job now (Quartz in the host); <see cref="IrlCodes.JobsDisabled"/> when jobs are off.</summary>
public interface IIrlJobTrigger
{
    Task<Result> TriggerAsync(CancellationToken ct);
}

public interface IIrlRunLog
{
    /// <summary>Starts a run, or returns <c>null</c> when one is already running: the database holds at most one running row. A run still running an hour on is closed as failed first.</summary>
    Task<IrlRunView?> TryStartAsync(CancellationToken ct);

    Task FinishAsync(IrlRunView run, CancellationToken ct);

    Task<bool> IsRunningAsync(CancellationToken ct);

    /// <summary>The most recent runs first.</summary>
    Task<IReadOnlyList<IrlRunView>> RecentAsync(int count, CancellationToken ct);

    /// <summary>One page of runs, newest first, and the total number of runs.</summary>
    Task<(IReadOnlyList<IrlRunView> Items, int Total)> PageAsync(int skip, int take, CancellationToken ct);

    /// <summary>Deletes finished runs started before <paramref name="before"/>.</summary>
    Task<int> PurgeBeforeAsync(Instant before, CancellationToken ct);
}

/// <summary>Rental spec §3: the stored IRL series, <c>realestate.irl</c>, one value per quarter.</summary>
public interface IIrlStore
{
    /// <summary>Every stored value, by quarter.</summary>
    Task<IReadOnlyList<IrlValue>> ListAsync(CancellationToken ct);

    /// <summary>One page of values, newest quarter first, and the total number of values.</summary>
    Task<(IReadOnlyList<IrlValue> Items, int Total)> PageAsync(int skip, int take, CancellationToken ct);

    /// <summary>Stores the quarter's value, replacing the one stored (the collector decides what may replace what).</summary>
    Task UpsertAsync(IrlValue value, CancellationToken ct);

    /// <summary>Stores a manual value in one statement unless the quarter holds an <c>insee</c> one; false when it did not write.</summary>
    Task<bool> UpsertManualAsync(IrlValue value, CancellationToken ct);
}
