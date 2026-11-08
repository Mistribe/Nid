using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Investments.Domain;

/// <summary>Spec §5.2: why a holding is « pas à jour », and why an envelope has no estimate.</summary>
public static class EstimateReasons
{
    public const string NoPrice = "no_price";
    public const string StalePrice = "stale_price";
    public const string RejectedPrice = "rejected_price";
    public const string NotFound = "not_found";
    public const string AutoPriceOff = "auto_price_off";
    public const string RateMissing = "rate_missing";
    public const string NoUnits = "no_units";

    public const string Disabled = "disabled";
    public const string NoSnapshot = "no_snapshot";
    public const string IncompleteSnapshot = "incomplete_snapshot";
    public const string NewerValuation = "newer_valuation";
    public const string NoNewerPrice = "no_newer_price";
    public const string Unreadable = "unreadable";
}

/// <summary>
/// The latest price of a holding's ISIN, already in the envelope's currency, or the <see cref="Reason"/> the caller could not use it
/// (<c>rejected_price</c>, <c>not_found</c>, <c>rate_missing</c>). <see cref="PriceOn"/> gives the latest accepted price on or before a date.
/// <see cref="Converted"/>: the price was quoted in another currency.
/// </summary>
public sealed record HoldingPrice(decimal? Price, LocalDate? OnDate, string? Reason, Func<LocalDate, decimal?> PriceOn, bool Converted = false);

public sealed record EstimateLine(Guid HoldingId, string Reason);

/// <summary>A holding counted in the estimate and its value: units × price (to the cent) or kept (statement + movements since).</summary>
public sealed record EstimatedLine(Guid HoldingId, decimal Value);

/// <summary><see cref="Converted"/>: a holding priced in the estimate had a converted price. Σ <see cref="Lines"/> + the unallocated part rounds to <see cref="Value"/>.</summary>
public sealed record EnvelopeEstimateResult(
    decimal? Value, LocalDate? AsOf, LocalDate? BasedOn, bool Converted, IReadOnlyList<EstimateLine> Stale, string? MissingReason, IReadOnlyList<EstimatedLine> Lines);

/// <summary>Spec §5.2: an envelope's value today from its latest complete snapshot, the movements since and the latest prices. Writes nothing (D1).</summary>
public static class EnvelopeEstimate
{
    public const int FreshDays = 7;

    /// <param name="prices">An entry for every holding with an ISIN and units.</param>
    public static EnvelopeEstimateResult Of(
        IReadOnlyList<HoldingSnapshot> snapshots, decimal? valuationAtSnapshot, LocalDate? latestValuationOn, IReadOnlyList<EnvelopeHolding> holdings,
        IReadOnlyList<HoldingMovement> movements, IReadOnlyDictionary<Guid, HoldingPrice> prices, LocalDate today)
    {
        var s = snapshots.Where(x => x.AsOf <= today).MaxBy(x => x.AsOf);
        if (s is null)
            return Missing(EstimateReasons.NoSnapshot);
        if (!s.Complete)
            return Missing(EstimateReasons.IncompleteSnapshot);
        if (latestValuationOn > s.AsOf)
            return Missing(EstimateReasons.NewerValuation);

        var since = movements.Where(m => m.OccurredOn > s.AsOf && m.OccurredOn <= today).ToList();
        var unallocated = (valuationAtSnapshot ?? HoldingMath.Total(s)) - HoldingMath.Total(s); // kept as it is
        var used = new List<LocalDate>();
        var stale = new List<EstimateLine>();
        var lines = new List<EstimatedLine>();
        var converted = false;
        foreach (var h in holdings)
        {
            // D16: a holding opened after the snapshot starts from nothing once it has a movement.
            var line = s.Lines.FirstOrDefault(l => l.HoldingId == h.Id)
                ?? (since.Any(m => m.HoldingId == h.Id) ? new SnapshotLine(h.Id, 0m, null, 0m, null) : null);
            if (line is null)
                continue;
            var kept = line.Value + HoldingMath.NetIn(h.Id, since, _ => true);
            if (!HoldingWire.HasUnits(h.Kind))
            {
                lines.Add(new EstimatedLine(h.Id, kept));
                continue;
            }
            var price = prices.GetValueOrDefault(h.Id);
            // §2.2: « Cours automatique » has no effect without an ISIN.
            var reason = h.Isin is not null && !h.AutoPrice ? EstimateReasons.AutoPriceOff
                : price is null ? EstimateReasons.NoPrice
                : price.Reason ?? Freshness(price, s.AsOf, today);
            if (reason is null && price is { Price: { } p, OnDate: { } on }
                && HoldingMath.UnitsAt(line, since, s.AsOf, today, price.PriceOn) is { } units)
            {
                lines.Add(new EstimatedLine(h.Id, Math.Round(units * p, 2, MidpointRounding.AwayFromZero)));
                used.Add(on);
                converted |= price.Converted;
                continue;
            }
            lines.Add(new EstimatedLine(h.Id, kept));
            stale.Add(new EstimateLine(h.Id, reason ?? EstimateReasons.NoUnits));
        }
        var total = unallocated + lines.Sum(l => l.Value);
        // Without a newer price the stale lines still say why each holding was not valued.
        return used.Count == 0
            ? new EnvelopeEstimateResult(null, null, s.AsOf, false, stale, EstimateReasons.NoNewerPrice, lines)
            : new EnvelopeEstimateResult(Math.Round(total, 2, MidpointRounding.AwayFromZero), used.Max(), s.AsOf, converted, stale, null, lines);
    }

    /// <summary>A price dated on or before the snapshot is no newer price; one more than <see cref="FreshDays"/> days old is stale, converted or not.</summary>
    private static string? Freshness(HoldingPrice price, LocalDate snapshotOn, LocalDate today)
        => price.OnDate is not { } on || on <= snapshotOn ? EstimateReasons.NoPrice
            : on < today.PlusDays(-FreshDays) ? EstimateReasons.StalePrice
            : price.Price is null ? EstimateReasons.NoPrice
            : null;

    private static EnvelopeEstimateResult Missing(string reason) => new(null, null, null, false, [], reason, []);
}
