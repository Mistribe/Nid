using NodaTime;

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

/// <summary>IRL collection settings (rental spec §3): bound from the <c>Irl</c> section by the host, defaults otherwise.</summary>
public sealed class IrlOptions
{
    public string WeeklyCron { get; set; } = "0 0 7 ? * MON";

    /// <summary>The whole series (a few hundred observations at most): no <c>lastNObservations</c>.</summary>
    public string SourceUrl { get; set; } = "https://bdm.insee.fr/series/sdmx/data/SERIES_BDM/001515333";
}

/// <summary>A stored IRL value as the admin page shows it: the quarter as <c>2026-Q2</c>, the value as a decimal string.</summary>
public sealed record IrlValueView(string Quarter, string Value, LocalDate? PublishedOn, LocalDate FetchedOn, string Source);

public sealed record IrlValuePage(IReadOnlyList<IrlValueView> Items, int Total, int Page)
{
    public const int PageSize = 50;
}

public sealed record IrlRunPage(IReadOnlyList<IrlRunView> Items, int Total, int Page)
{
    public const int PageSize = 50;
}

public sealed record IrlSyncRequested(bool Requested);

/// <summary>Rental spec §3: one collection of the IRL series, <c>realestate.irl_run</c>; <c>Status</c> is <c>running</c>, <c>succeeded</c> or <c>failed</c>.</summary>
public sealed record IrlRunView(Guid Id, Instant StartedAt, Instant? FinishedAt, string Status, int Added, int Replaced, int Rejected, string? Message);
