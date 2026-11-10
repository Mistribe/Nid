using System.Globalization;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Application.Fx;

/// <summary>Rates are decimal strings on the wire, like amounts: a JSON number would round them in the browser.</summary>
public sealed record FxRateView(LocalDate OnDate, string Quote, string Rate, string Source)
{
    public static FxRateView From(FxRate rate)
        => new(rate.OnDate, rate.Quote.Code, WireDecimal(rate.Rate), rate.Source.ToWire());

    /// <summary>
    /// The <c>numeric(19,10)</c> column always round-trips with ten decimal places (e.g. <c>0.8612000000</c>);
    /// dividing by 1 re-normalizes a decimal to its minimal exact representation before it goes on the wire.
    /// </summary>
    internal static string WireDecimal(decimal value)
        => (value / 1.000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
}

public sealed record FxRejectionView(LocalDate OnDate, string Quote, string Rate, string? Previous, string Reason);

public sealed record FxSyncRunView(
    Guid Id, string Kind, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Status,
    int FilesRead, int RowsWritten, string? FailureCode, IReadOnlyList<FxRejectionView> Rejected)
{
    public static FxSyncRunView From(FxSyncRun run)
        => new(run.Id,
            run.Kind == FxRunKind.Daily ? "daily" : "backfill",
            run.StartedAt.ToDateTimeOffset(), run.FinishedAt?.ToDateTimeOffset(),
            run.Status.ToString().ToLowerInvariant(),
            run.FilesRead, run.RowsWritten, run.FailureCode,
            run.Rejected.Select(r => new FxRejectionView(r.OnDate, r.Quote,
                r.Rate.ToString(CultureInfo.InvariantCulture), r.Previous?.ToString(CultureInfo.InvariantCulture), r.Reason)).ToList());
}

public sealed record FxSyncRunPage(IReadOnlyList<FxSyncRunView> Items, int Total, int Page)
{
    public const int PageSize = 50;
}

public sealed record FxSyncRequested(bool Requested);
