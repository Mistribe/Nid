using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using NodaTime;
using NodaTime.Text;

namespace Nid.Subscriptions.Application;

/// <summary>A total in the reference currency, or none (<paramref name="Total"/> null) with the rates it lacked.</summary>
public sealed record ConvertedTotal(Money? Total, bool IsEstimate, IReadOnlyList<Finding> Missing)
{
    public static ConvertedTotal None { get; } = new(null, false, []);
}

/// <summary>The rates a set of pieces needs: <see cref="IFxRates.LoadAsync"/>'s arguments.</summary>
public sealed record RateNeed(IReadOnlyCollection<Currency> Quotes, LocalDate From, LocalDate To);

/// <summary>1C2a design §5: each piece at the rate of its charge's date; no total as soon as one piece has no rate.</summary>
public static class FxConversion
{
    public static RateNeed? Need(IEnumerable<CostPiece> pieces, Currency reference)
    {
        var foreign = pieces.Where(p => p.Currency != reference).ToList();
        if (foreign.Count == 0)
            return null;

        var quotes = foreign.Select(p => p.Currency).Append(reference).Distinct().ToList();
        return new RateNeed(quotes, foreign.Min(p => p.ChargeDate), foreign.Max(p => p.ChargeDate));
    }

    public static ConvertedTotal Convert(IEnumerable<CostPiece> pieces, RateTable rates, Currency reference)
    {
        var total = 0m;
        var estimate = false;
        var missing = new SortedDictionary<string, Finding>(StringComparer.Ordinal);

        foreach (var piece in pieces)
        {
            if (!rates.TryConvert(piece.Amount, piece.Currency, reference, piece.ChargeDate, piece.IsProjected, out var converted, out var miss))
            {
                var key = $"{miss.Currency.Code}:{LocalDatePattern.Iso.Format(miss.Date)}";
                missing.TryAdd(key, new Finding(FxCodes.RateMissing,
                    $"No {miss.Currency.Code} rate within {rates.MaxGapDays} days before {LocalDatePattern.Iso.Format(miss.Date)}.", key));
                continue;
            }

            total += converted;
            estimate |= piece.IsProjected && piece.Currency != reference;
        }

        if (missing.Count > 0)
            return new ConvertedTotal(null, false, missing.Values.ToList());

        return Money.Create(decimal.Round(total, Money.MaxDecimals), reference).TryGetValue(out var money)
            ? new ConvertedTotal(money, estimate, [])
            : ConvertedTotal.None;
    }
}
