using Nid.Core.Contracts;

namespace Nid.Core.Domain.Fx;

/// <summary>Design §4.4: a collected rate that moved more than the threshold since the previous stored one is refused, for its quote only.</summary>
public static class FxPlausibility
{
    public static FxReading Check(IReadOnlyList<FxRate> candidates, IReadOnlyDictionary<Currency, decimal> previous, decimal maxChange)
    {
        var accepted = new List<FxRate>(candidates.Count);
        var rejected = new List<FxRejection>();
        foreach (var rate in candidates)
        {
            if (previous.TryGetValue(rate.Quote, out var before) && Math.Abs(rate.Rate / before - 1m) > maxChange)
                rejected.Add(new FxRejection(rate.OnDate, rate.Quote.Code, rate.Rate, before, FxCodes.ImplausibleChange));
            else
                accepted.Add(rate);
        }

        return new FxReading(accepted, rejected);
    }
}
