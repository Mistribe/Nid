using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Contracts;

public static class IrlCodes
{
    public const string SourceUnavailable = "realestate.irl_source_unavailable";
    public const string SourceMalformed = "realestate.irl_source_malformed";
    public const string SourceRefused = "realestate.irl_source_refused";
    public const string SyncRunning = "realestate.irl_sync_running";
    public const string Disabled = "realestate.irl_disabled";
    public const string RunInterrupted = "realestate.irl_run_interrupted";
    public const string JobsDisabled = "realestate.irl_jobs_disabled";
    public const string IrlFromInsee = "realestate.irl_from_insee";
    public const string InvalidIrlValue = "realestate.invalid_irl_value";
}

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

/// <summary>IRL collection settings (rental spec §3): bound from the <c>Irl</c> section by the host, defaults otherwise.</summary>
public sealed class IrlOptions
{
    public string WeeklyCron { get; set; } = "0 0 7 ? * MON";

    /// <summary>The whole series (a few hundred observations at most): no <c>lastNObservations</c>.</summary>
    public string SourceUrl { get; set; } = "https://bdm.insee.fr/series/sdmx/data/SERIES_BDM/001515333";
}

/// <summary>A stored IRL value as the admin page shows it: the quarter as <c>2026-Q2</c>, the value as a decimal string.</summary>
public sealed record IrlValueView(string Quarter, string Value, LocalDate? PublishedOn, LocalDate FetchedOn, string Source);

public sealed record IrlAdminView(IReadOnlyList<IrlValueView> Values, IReadOnlyList<IrlRunView> Runs);

public sealed record IrlSyncRequested(bool Requested);

/// <summary>Rental spec §3: one collection of the IRL series, <c>realestate.irl_run</c>; <c>Status</c> is <c>running</c>, <c>succeeded</c> or <c>failed</c>.</summary>
public sealed record IrlRunView(Guid Id, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Status, int Added, int Replaced, int Rejected, string? Message);

public interface IIrlRunLog
{
    /// <summary>Starts a run, or returns <c>null</c> when one is already running: the database holds at most one running row. A run still running an hour on is closed as failed first.</summary>
    Task<IrlRunView?> TryStartAsync(CancellationToken ct);

    Task FinishAsync(IrlRunView run, CancellationToken ct);

    Task<bool> IsRunningAsync(CancellationToken ct);

    /// <summary>The most recent runs first.</summary>
    Task<IReadOnlyList<IrlRunView>> RecentAsync(int count, CancellationToken ct);

    /// <summary>Deletes finished runs started before <paramref name="before"/>.</summary>
    Task<int> PurgeBeforeAsync(Instant before, CancellationToken ct);
}
