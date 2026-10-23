using NodaTime;

namespace Nid.Investments.Domain.Prices;

public enum PriceVerdict { Accepted, Rejected, Invalid }

/// <summary>A collected price is refused when it is not a usable quote, or when it moved more than the threshold since the stored reference.</summary>
public static class PricePlausibility
{
    /// <summary>The largest price <c>security_price.price</c> (<c>numeric(20,6)</c>) can store.</summary>
    private const decimal MaxPrice = 100_000_000_000_000m;

    public static PriceVerdict Check(PriceReading reading, decimal? reference, LocalDate today, decimal maxChange, int maxAgeDays)
    {
        if (reading.Price is <= 0m or >= MaxPrice || reading.OnDate > today || Period.Between(reading.OnDate, today, PeriodUnits.Days).Days > maxAgeDays)
            return PriceVerdict.Invalid;
        if (reference is { } before && Math.Abs(reading.Price / before - 1m) > maxChange)
            return PriceVerdict.Rejected;
        return PriceVerdict.Accepted;
    }
}
