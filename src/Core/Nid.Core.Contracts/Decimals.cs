using System.Globalization;

namespace Nid.Core.Contracts;

/// <summary>
/// A figure computed in <c>double</c> (a growth factor from <see cref="Math.Pow(double, double)"/>, a solved rate) brought back to
/// <c>decimal</c>. .NET 11 converts a <c>double</c> exactly, binary noise included (1.01 becomes 1.0100000000000000088817841970);
/// .NET 10 rounded to 15 significant digits. Every such conversion goes through here, so figures stay what they were.
/// </summary>
public static class Decimals
{
    /// <summary>The value rounded to 15 significant digits, as a cast did before .NET 11; throws <see cref="OverflowException"/> like a
    /// cast for a value <c>decimal</c> cannot hold, NaN and infinities included.</summary>
    public static decimal FromDouble(double value)
    {
        if (!double.IsFinite(value)) throw new OverflowException("Value was either too large or too small for a Decimal.");
        return decimal.Parse(value.ToString("G15", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
