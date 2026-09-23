using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Domain.Fx;

public sealed record FxReading(IReadOnlyList<FxRate> Rates, IReadOnlyList<FxRejection> Rejected);

/// <summary>Normalization on write (<c>core-data-model.md</c> §8.2): every stored rate is units for 1 USD.</summary>
public static class FxRebase
{
    /// <summary>The largest value <c>numeric(19,10)</c> holds: nine integer digits.</summary>
    public const decimal MaxRate = 999_999_999.9999999999m;

    /// <param name="perBase">Units of each currency for 1 <paramref name="baseCurrency"/>; must hold USD unless the base is USD.</param>
    public static FxReading ToUsd(LocalDate date, Currency baseCurrency, IReadOnlyDictionary<Currency, decimal> perBase, FxRateSource source)
    {
        var usdPerBase = baseCurrency == Currency.Usd ? 1m : perBase[Currency.Usd];
        var rates = new List<FxRate>(perBase.Count);
        var rejected = new List<FxRejection>();

        void Add(Currency quote, decimal rate)
        {
            var rounded = decimal.Round(rate, 10);
            if (rounded <= 0m || rounded > MaxRate)
                rejected.Add(new FxRejection(date, quote.Code, rate, null, FxCodes.OutOfRange));
            else
                rates.Add(new FxRate(date, quote, rounded, source));
        }

        foreach (var (quote, value) in perBase)
            if (quote != Currency.Usd && quote != baseCurrency)
                Add(quote, value / usdPerBase);

        if (baseCurrency != Currency.Usd)
            Add(baseCurrency, 1m / usdPerBase);

        return new FxReading(rates, rejected);
    }
}
