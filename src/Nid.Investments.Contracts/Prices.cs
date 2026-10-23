using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Investments.Contracts;

public enum PriceStatus { Accepted, Rejected }

/// <summary>The wire values of <c>security_price.source</c>.</summary>
public static class PriceSources
{
    public const string Boursorama = "boursorama";
    public const string Yahoo = "yahoo";
}

public static class PriceCodes
{
    public const string SourceUnavailable = "investments.price_source_unavailable";
    public const string SourceMalformed = "investments.price_source_malformed";
    public const string SourceRefused = "investments.price_source_refused";
    public const string SyncRunning = "investments.price_sync_running";
    public const string Disabled = "investments.prices_disabled";
    public const string RunInterrupted = "investments.price_run_interrupted";
    public const string RunAbandoned = "investments.price_run_abandoned";
    public const string JobsDisabled = "investments.price_jobs_disabled";
}

/// <summary>Where an ISIN is found at each source (holding prices spec §2.3); <see cref="NotFoundAt"/> is set when no source knows it.</summary>
public sealed record SecurityListing(string Isin, string? BoursoramaPath, string? YahooSymbol, Instant ResolvedAt, Instant? NotFoundAt);

/// <summary>A quote as the source gives it, on the source's date, in the source's currency (spec §2.4).</summary>
public sealed record SecurityPrice(
    string Isin, LocalDate OnDate, decimal Price, Currency Currency, string Source, PriceStatus Status, decimal? Previous, Instant FetchedAt,
    PersonId? AcceptedBy);

/// <summary>The unit price of a holding's latest snapshot line, with the envelope it is in (its currency is the caller's to resolve).</summary>
public sealed record SnapshotPrice(string Isin, decimal UnitPrice, ItemId Envelope, LocalDate AsOf);

public interface ISecurityPriceStore
{
    Task<IReadOnlyDictionary<string, SecurityListing>> ListingsAsync(IReadOnlyCollection<string> isins, CancellationToken ct);

    /// <summary>Creates the listing or replaces the one of that ISIN.</summary>
    Task SaveListingAsync(SecurityListing listing, CancellationToken ct);

    /// <summary>Per ISIN, its latest <see cref="PriceStatus.Accepted"/> price; an ISIN without one is absent.</summary>
    Task<IReadOnlyDictionary<string, SecurityPrice>> LatestAcceptedAsync(IReadOnlyCollection<string> isins, CancellationToken ct);

    /// <summary>Creates the price or replaces the one of that ISIN and date; false when it kept a row a person accepted.</summary>
    Task<bool> WriteAsync(SecurityPrice price, CancellationToken ct);

    /// <summary>Deletes the prices dated before <paramref name="before"/>.</summary>
    Task<int> PurgeBeforeAsync(LocalDate before, CancellationToken ct);
}

public enum PriceRunStatus { Running, Succeeded, Failed }

/// <summary>One nightly collection, <c>investments.price_run</c> (spec §2.5): counters only, no ISIN.</summary>
public sealed record PriceRun(
    Guid Id, Instant StartedAt, Instant? FinishedAt, PriceRunStatus Status, int Queried, int Accepted, int Rejected, int NotFound, int SourceErrors,
    string? FailureCode, string? FailureMessage);

public interface IPriceRunLog
{
    /// <summary>
    /// Starts a run, or returns <c>null</c> when one is already running: the database holds at most one running row.
    /// A run still marked running an hour after it started is closed as <see cref="PriceCodes.RunAbandoned"/> first.
    /// </summary>
    Task<PriceRun?> TryStartAsync(CancellationToken ct);

    Task FinishAsync(PriceRun run, CancellationToken ct);

    Task<bool> IsRunningAsync(CancellationToken ct);

    /// <summary>The most recent runs first.</summary>
    Task<IReadOnlyList<PriceRun>> RecentAsync(int count, CancellationToken ct);

    /// <summary>Deletes finished runs started before <paramref name="before"/>.</summary>
    Task<int> PurgeBeforeAsync(Instant before, CancellationToken ct);
}

/// <summary>The bytes a source answered; <c>null</c> is a 404 (the source does not know the page).</summary>
public sealed record PriceFetch(byte[]? Body);

/// <summary>Fetches the raw bytes of the price sources through the outbound client; never throws, a failure is a <see cref="PriceCodes"/> code.</summary>
public interface IPriceSourceClient
{
    Task<Result<PriceFetch>> BoursoramaSearchAsync(string isin, CancellationToken ct);

    /// <param name="path">A path found by the search, e.g. <c>/bourse/trackers/cours/1rTCW8/</c>.</param>
    Task<Result<PriceFetch>> BoursoramaPageAsync(string path, CancellationToken ct);

    Task<Result<PriceFetch>> YahooSearchAsync(string isin, CancellationToken ct);

    Task<Result<PriceFetch>> YahooChartAsync(string symbol, CancellationToken ct);
}

/// <summary>Starts the <c>prices-daily</c> job now (Quartz in the host); <see cref="PriceCodes.JobsDisabled"/> when jobs are off.</summary>
public interface IPriceJobTrigger
{
    Task<Result> TriggerAsync(CancellationToken ct);
}

/// <summary>Holding prices settings (spec §2): bound from configuration by the host, defaults otherwise.</summary>
public sealed class PriceOptions
{
    public string DailyCron { get; set; } = "0 0 21 ? * MON-FRI";

    public TimeSpan RequestSpacing { get; set; } = TimeSpan.FromSeconds(1);

    public decimal MaxChange { get; set; } = 0.20m;

    public int MaxAgeDays { get; set; } = 10;

    public int NotFoundRetryDays { get; set; } = 7;

    public string BoursoramaBaseUrl { get; set; } = "https://www.boursorama.com";

    public string YahooBaseUrl { get; set; } = "https://query2.finance.yahoo.com";
}
