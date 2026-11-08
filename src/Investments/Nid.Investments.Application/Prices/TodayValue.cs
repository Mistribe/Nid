using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;

namespace Nid.Investments.Application.Prices;

/// <summary>Holding prices spec §5.4: an envelope's value today, its estimate when newer than its latest valuation, else that valuation.</summary>
public static class TodayValue
{
    /// <summary><paramref name="figures"/> must have a value; <c>Lines</c> is the estimate's, null for the valuation.</summary>
    public static (Money Value, LocalDate AsOf, bool Estimated, IReadOnlyList<EstimatedLine>? Lines) Pick(EnvelopeFigures figures, EnvelopeEstimate? estimate)
        => estimate is { Value: { } value, AsOf: { } asOf } && asOf > figures.AsOf
            ? (value, asOf, true, estimate.Lines)
            : (figures.Value ?? throw new ArgumentException("The figures have no value.", nameof(figures)), figures.AsOf!.Value, false, null);
}
