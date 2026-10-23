using NodaTime;
using static Nid.Core.Domain.Tax.SaleRounding;

namespace Nid.Core.Domain.Tax;

/// <summary>Gain and taxes on a PEA or brokerage account sold today, every amount rounded to the euro.</summary>
public sealed record SecuritiesSaleResult(
    decimal Gain, decimal IncomeTax, decimal SocialTax, decimal Net, bool Approximate, IReadOnlyList<string> Notes);

public sealed record PricedLine(decimal Value, decimal Units, decimal CostPerUnit);

/// <summary>A brokerage account, or a PEA under 5 years (<c>IsPea</c>), whose gain nets with the others (D14).</summary>
public sealed record NettableGain(Guid Item, decimal Gain, bool IsPea);

/// <summary>Sale tax spec §4.2, §4.5, D6, D14, D17: PEA and brokerage account (art. 150-0 A et 200 A CGI).</summary>
public static class SecuritiesSale
{
    /// <summary><paramref name="remainingCapital"/> comes from <see cref="RemainingCapital"/>.</summary>
    public static SecuritiesSaleResult Pea(
        LocalDate openedOn, decimal value, decimal remainingCapital, bool approximate, LocalDate today, SaleTaxRules rules)
    {
        // art. 150-0 A II 2 CGI: the 5-year count starts at the opening; the 5th anniversary makes the plan 5 years old.
        var underFive = HeldYears.FullYears(openedOn, today) < 5;
        var notes = new List<string>();
        if (openedOn < new LocalDate(2018, 1, 1)) notes.Add("pea_pre_2018_rates_not_modelled"); // D17
        if (underFive) notes.Add("pea_closes_under_five_years");

        var gain = value - remainingCapital;
        var income = gain > 0m && underFive ? Euros(rules.FlatTaxRate * gain) : 0m;
        var social = gain > 0m ? Euros(rules.CapitalSocialRate * gain) : 0m;
        return new SecuritiesSaleResult(Euros(gain), income, social, Euros(value) - income - social, approximate, notes);
    }

    /// <summary>D6: with priced lines the gain is the sum of each line's value minus its cost; without, value minus net contributions.</summary>
    public static SecuritiesSaleResult Brokerage(decimal value, IReadOnlyList<PricedLine>? lines, decimal netContributions, SaleTaxRules rules)
    {
        var priced = lines is { Count: > 0 };
        var gain = priced ? lines!.Sum(l => l.Value - l.Units * l.CostPerUnit) : value - netContributions;
        var income = gain > 0m ? Euros(rules.FlatTaxRate * gain) : 0m;
        var social = gain > 0m ? Euros(rules.CapitalSocialRate * gain) : 0m;
        return new SecuritiesSaleResult(
            Euros(gain), income, social, Euros(value) - income - social, !priced, ["brokerage_scale_option"]);
    }

    /// <summary>
    /// D14: losses offset gains of the same kind before tax. Income tax nets over all the items (art. 150-0 D 11 CGI); social levies
    /// over the brokerage accounts only, a PEA's being due on its own gain (CSS L136-7 II 5°).
    /// </summary>
    public static IReadOnlyDictionary<Guid, (decimal IncomeTax, decimal SocialTax)> Netted(IReadOnlyList<NettableGain> gains, SaleTaxRules rules)
    {
        var income = Spread(gains, rules.FlatTaxRate);
        var social = Spread(gains.Where(g => !g.IsPea).ToList(), rules.CapitalSocialRate);
        return gains.ToDictionary(g => g.Item, g => (
            income.GetValueOrDefault(g.Item),
            g.IsPea ? (g.Gain > 0m ? Euros(rules.CapitalSocialRate * g.Gain) : 0m) : social.GetValueOrDefault(g.Item)));
    }

    // The positive net taxed once, spread over the items with a gain pro rata of their gain, the shares summing to the rounded tax.
    private static Dictionary<Guid, decimal> Spread(IReadOnlyList<NettableGain> gains, decimal rate)
    {
        var net = gains.Sum(g => g.Gain);
        if (net <= 0m) return [];
        var winners = gains.Where(g => g.Gain > 0m).ToList();
        var total = winners.Sum(g => g.Gain);
        var shares = Apportion(winners.Select(g => rate * net * g.Gain / total).ToList());
        return winners.Select((g, i) => (g.Item, shares[i])).ToDictionary();
    }
}
