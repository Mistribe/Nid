using System.Globalization;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Application.Fx;

/// <summary>Rates are decimal strings on the wire, like amounts: a JSON number would round them in the browser.</summary>
public sealed record FxRateView(LocalDate OnDate, string Quote, string Rate, string Source)
{
    public static FxRateView From(FxRate rate)
        => new(rate.OnDate, rate.Quote.Code, rate.Rate.ToString(CultureInfo.InvariantCulture), rate.Source.ToWire());
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

public sealed record FxSyncRequested(bool Requested);
