namespace Nid.Core.Domain.Tax;

/// <summary>
/// Where the household sits on the income tax scale: the bands, the taxable income per part (<c>Quotient</c>), the next band's start
/// above it and the distance to it (both null in the top band), and where a chart of the scale should stop (<c>DisplayEnd</c>).
/// </summary>
public sealed record TaxBracketPosition(
    IReadOnlyList<(decimal From, decimal? To, decimal Rate)> Bands, decimal Quotient, decimal? NextFrom, decimal? ToNext, decimal DisplayEnd)
{
    public static TaxBracketPosition Of(TaxRules rules, decimal taxable, decimal parts)
    {
        var b = rules.Brackets;
        var quotient = Math.Floor(taxable / parts);
        var bands = b.Select((x, i) => (x.From, i + 1 < b.Count ? (decimal?)b[i + 1].From : null, x.Rate)).ToList();
        decimal? next = b.Where(x => x.From > 0m && x.From >= quotient).Select(x => (decimal?)x.From).FirstOrDefault();
        var end = Math.Ceiling(Math.Max(b[^1].From * 1.25m, quotient * 1.1m) / 1_000m) * 1_000m;
        return new(bands, quotient, next, next - quotient, end);
    }
}
