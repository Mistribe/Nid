using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

public static class InflationCodes
{
    public const string SourceUnavailable = "core.cpi_source_unavailable";
    public const string SourceMalformed = "core.cpi_source_malformed";
    public const string SourceRefused = "core.cpi_source_refused";
    public const string SyncRunning = "core.cpi_sync_running";
    public const string Disabled = "core.cpi_disabled";
    public const string JobsDisabled = "core.cpi_jobs_disabled";
    public const string FromInsee = "core.cpi_from_insee";
    public const string InvalidValue = "core.invalid_cpi_value";
}

/// <summary>Why a real figure is absent.</summary>
public static class RealMissing
{
    public const string Unavailable = "unavailable";
    public const string Currency = "currency";
    public const string NotPublished = "not_published";
}

/// <summary>A real percent (one decimal) and the month the inflation stopped at ("2026-09"); <c>Missing</c> says why <c>Percent</c> is null.</summary>
public sealed record RealPercentView(decimal? Percent, string? InflationTo, string? Missing)
{
    /// <summary>The nominal figure is itself absent: nothing to deflate, nothing to explain.</summary>
    public static RealPercentView None => new(null, null, null);
}

/// <summary>
/// The observed monthly CPI, shared by every "real" figure. Fractions are exact (0.02 = 2 %); rounding belongs to the views.
/// A missing month gives null, never a guess.
/// </summary>
public sealed class InflationSeries(IReadOnlyDictionary<YearMonth, decimal> source)
{
    // A copy, so a later change to the caller's dictionary cannot move the series.
    private readonly Dictionary<YearMonth, decimal> index = new(source);

    public static readonly InflationSeries Empty = new(new Dictionary<YearMonth, decimal>());

    public YearMonth? LastPublished { get; } = source.Count == 0 ? null : source.Keys.Max();

    public decimal? Index(YearMonth month) => index.TryGetValue(month, out var v) ? v : null;

    /// <summary>CPI(to) ÷ CPI(from), null when either month is unknown.</summary>
    public decimal? Between(YearMonth from, YearMonth to) => Index(from) is { } a && Index(to) is { } b ? b / a : null;

    /// <summary>The change over the twelve months ending at the last stored month.</summary>
    public (decimal Rate, YearMonth To)? Last12Months =>
        LastPublished is { } to && Between(to.PlusMonths(-12), to) is { } r ? (r - 1, to) : null;

    /// <summary>Annual average of <paramref name="year"/> over that of the year before; null unless all 24 months are known.</summary>
    public decimal? Annual(int year) => Mean(year) is { } m && Mean(year - 1) is { } p ? m / p - 1 : null;

    /// <summary>(1 + nominal) ÷ (1 + inflation) − 1.</summary>
    public static decimal Real(decimal nominal, decimal inflation) => (1 + nominal) / (1 + inflation) - 1;

    /// <summary>The month, or the last published one when it is later; null on an empty series.</summary>
    public YearMonth? Clamp(YearMonth month) => LastPublished is { } last ? (month < last ? month : last) : null;

    public decimal? Deflator(YearMonth from, YearMonth to) =>
        Clamp(from) is { } f && Clamp(to) is { } t ? Between(f, t) : null;

    public RealPercentView RealChange(decimal? nominalPct, YearMonth from, YearMonth to, Currency currency)
    {
        if (nominalPct is not { } nominal) return RealPercentView.None;
        if (Gate(currency) is { } gate) return gate;
        var stop = Pattern(Clamp(to)!.Value);
        return Deflator(from, to) is { } d
            ? new(Round1(Real(nominal / 100, d - 1) * 100), stop, null)
            : new(null, stop, RealMissing.NotPublished);
    }

    public RealPercentView RealChangeOver12Months(decimal? nominalPct, Currency currency)
    {
        if (nominalPct is not { } nominal) return RealPercentView.None;
        if (Gate(currency) is { } gate) return gate;
        return Last12Months is { } l
            ? new(Round1(Real(nominal / 100, l.Rate) * 100), Pattern(l.To), null)
            : new(null, Pattern(LastPublished!.Value), RealMissing.NotPublished);
    }

    /// <summary>The XIRR of the flows deflated into euros of the month of <paramref name="at"/> (clamped).</summary>
    public RealPercentView RealAnnualReturn(IEnumerable<(LocalDate On, decimal Amount)> cash, LocalDate at, Currency currency)
    {
        var flows = cash.ToList();
        if (Xirr.AnnualPercent(flows, at) is null) return RealPercentView.None;
        if (Gate(currency) is { } gate) return gate;
        var stop = Clamp(new YearMonth(at.Year, at.Month))!.Value;
        var deflated = new List<(LocalDate On, decimal Amount)>();
        foreach (var (on, amount) in flows)
        {
            if (Deflator(new YearMonth(on.Year, on.Month), stop) is not { } d) return new(null, Pattern(stop), RealMissing.NotPublished);
            deflated.Add((on, amount * d));
        }
        return new(Xirr.AnnualPercent(deflated, at), Pattern(stop), null);
    }

    /// <summary>The mean of the quarter's three months; null when any is missing.</summary>
    public decimal? QuarterMean(int year, int quarter)
    {
        var months = Enumerable.Range(0, 3).Select(i => Index(new YearMonth(year, (quarter - 1) * 3 + 1 + i))).ToList();
        return months.All(v => v is not null) ? months.Average(v => v!.Value) : null;
    }

    private RealPercentView? Gate(Currency currency) =>
        currency != Currency.Eur ? new(null, null, RealMissing.Currency)
        : LastPublished is null ? new(null, null, RealMissing.Unavailable)
        : null;

    private static string Pattern(YearMonth m) => YearMonthPattern.Iso.Format(m);

    private static decimal Round1(decimal x) => Math.Round(x, 1, MidpointRounding.AwayFromZero);

    private decimal? Mean(int year)
    {
        var months = Enumerable.Range(1, 12).Select(m => Index(new YearMonth(year, m))).ToList();
        return months.All(v => v is not null) ? months.Average(v => v!.Value) : null;
    }
}

public interface IInflation
{
    Task<InflationSeries> ReadAsync(CancellationToken ct);
}

/// <summary>Fetches the raw SDMX document of the INSEE series through the outbound client; never throws, a failure is an <see cref="InflationCodes"/> code.</summary>
public interface ICpiSourceClient
{
    Task<Result<byte[]>> FetchAsync(CancellationToken ct);
}

/// <summary>Starts the <c>cpi-weekly</c> job now (Quartz in the host); <see cref="InflationCodes.JobsDisabled"/> when jobs are off.</summary>
public interface ICpiJobTrigger
{
    Task<Result> TriggerAsync(CancellationToken ct);
}

/// <summary>CPI collection settings: bound from the <c>Inflation</c> section by the host, defaults otherwise.</summary>
public sealed class InflationOptions
{
    public string WeeklyCron { get; set; } = "0 0 7 ? * MON";

    /// <summary>INSEE BDM 011814630 (IPC base 2025, monthly), the whole series.</summary>
    public string SourceUrl { get; set; } = "https://bdm.insee.fr/series/sdmx/data/SERIES_BDM/011814630";
}

/// <summary>A stored CPI value as the admin page shows it: the month as <c>2026-09</c>, the value as a decimal string.</summary>
public sealed record CpiValueView(string Month, string Value, LocalDate? PublishedOn, LocalDate FetchedOn, string Source);

public sealed record CpiAdminView(IReadOnlyList<CpiValueView> Values, IReadOnlyList<CpiRunView> Runs);

public sealed record CpiSyncRequested(bool Requested);

/// <summary>One collection of the CPI series, <c>core.cpi_run</c>; <c>Status</c> is <c>running</c>, <c>succeeded</c> or <c>failed</c>.</summary>
public sealed record CpiRunView(Guid Id, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Status, int Added, int Replaced, int Rejected, string? Message);

public interface ICpiRunLog
{
    /// <summary>Starts a run, or returns <c>null</c> when one is already running: the database holds at most one running row. A run still running an hour on is closed as failed first.</summary>
    Task<CpiRunView?> TryStartAsync(CancellationToken ct);

    Task FinishAsync(CpiRunView run, CancellationToken ct);

    Task<bool> IsRunningAsync(CancellationToken ct);

    /// <summary>The most recent runs first.</summary>
    Task<IReadOnlyList<CpiRunView>> RecentAsync(int count, CancellationToken ct);

    /// <summary>Deletes finished runs started before <paramref name="before"/>.</summary>
    Task<int> PurgeBeforeAsync(Instant before, CancellationToken ct);
}
