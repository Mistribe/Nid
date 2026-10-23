using NodaTime;
using static Nid.Core.Domain.Tax.SaleRounding;

namespace Nid.Core.Domain.Tax;

/// <summary>Remaining premiums by period today and at 31 December of the previous year, from <see cref="RemainingCapital"/>.</summary>
public sealed record LifeContract(
    Guid Item, LocalDate OpenedOn, decimal Value, decimal RemainingBefore2017, decimal RemainingAfter2017,
    decimal YearEndBefore2017, decimal YearEndAfter2017, IReadOnlyList<SaleOwner> Owners);

/// <summary>
/// The gain in its three parts: pre-27/09/2017 premiums, post-2017 premiums at the reduced rate, at the flat rate.
/// Every amount rounded to the euro.
/// </summary>
public sealed record ContractSaleTax(
    Guid Item, int YearsHeld, decimal Gain, decimal GainBefore2017, decimal GainReduced, decimal GainFull, decimal AllowanceUsed,
    decimal IncomeTax, decimal SocialTax, decimal Net);

/// <summary>Sale tax spec §4.3, D3, D8d, D12, D13: life insurance contracts fully surrendered today (art. 125-0 A CGI).</summary>
public static class LifeInsuranceSurrender
{
    // art. 125-0 A CGI: the allowance and the 7,5 % rate need 8 years from the opening.
    private const int AllowanceYears = 8;

    /// <summary>
    /// <paramref name="taxed"/> ⊆ <paramref name="visible"/>: the threshold counts each holder's visible contracts (D8d); the allowance
    /// is shared by the taxed contracts of 8 years or more, so a fiche passes its contract alone with the whole allowance (D3).
    /// </summary>
    public static IReadOnlyList<ContractSaleTax> Compute(
        IReadOnlyList<LifeContract> taxed, IReadOnlyList<LifeContract> visible, decimal allowance, LocalDate today, SaleTaxRules rules)
    {
        // D12: the share of a holder's post-2017 premiums under the threshold, premiums at 31 December of the previous year.
        decimal UnderThreshold(Guid person)
        {
            var held = visible.SelectMany(c => c.Owners.Where(o => o.Person == person), (c, o) => (c, Share: o.ShareBp / 10_000m)).ToList();
            var before = held.Sum(h => h.c.YearEndBefore2017 * h.Share);
            var after = held.Sum(h => h.c.YearEndAfter2017 * h.Share);
            // The threshold tests all premiums: pre-2017 ones over it leave no room for later ones.
            if (after <= 0m) return before <= rules.LifeInsurancePremiumThreshold ? 1m : 0m;
            return Math.Min(1m, Math.Max(0m, rules.LifeInsurancePremiumThreshold - before) / after);
        }

        // Each contract's gain in its three parts, taxed at rates[p]; under 8 years the whole gain is at the flat rate.
        decimal[] rates = [rules.LifeInsuranceReducedRate, rules.LifeInsuranceReducedRate, rules.FlatTaxRate];
        var contracts = taxed.Select(c =>
        {
            var years = HeldYears.FullYears(c.OpenedOn, today);
            var gain = c.Value - c.RemainingBefore2017 - c.RemainingAfter2017;
            var eligible = years >= AllowanceYears;
            decimal[] parts = [0m, 0m, 0m];
            if (gain > 0m && !eligible) parts[2] = gain;
            else if (gain > 0m)
            {
                var premiums = c.RemainingBefore2017 + c.RemainingAfter2017;
                parts[0] = premiums > 0m ? gain * c.RemainingBefore2017 / premiums : 0m;
                var after = gain - parts[0];
                parts[1] = after * c.Owners.Sum(o => o.ShareBp * UnderThreshold(o.Person)) / 10_000m;
                parts[2] = after - parts[1];
            }
            return (Contract: c, Years: years, Gain: gain, Eligible: eligible, Parts: parts, Allowance: new decimal[3]);
        }).ToList();

        // D13: the allowance goes to the pre-2017 part, then the 7,5 % part, then the 12,8 % part, each spread pro rata of its gains.
        var left = allowance;
        for (var p = 0; p < 3; p++)
        {
            var total = contracts.Where(c => c.Eligible).Sum(c => c.Parts[p]);
            var used = Math.Min(left, total);
            left -= used;
            foreach (var c in contracts.Where(c => c.Eligible && total > 0m)) c.Allowance[p] = c.Parts[p] * (used / total);
        }

        var allowances = Apportion(contracts.Select(c => c.Allowance.Sum()).ToList());
        return contracts.Select((c, i) =>
        {
            var parts = Apportion(c.Parts);
            var income = Euros(Enumerable.Range(0, 3).Sum(p => rates[p] * (c.Parts[p] - c.Allowance[p])));
            var social = c.Gain > 0m ? Euros(rules.LifeInsuranceSocialRate * c.Gain) : 0m;
            return new ContractSaleTax(
                c.Contract.Item, c.Years, Euros(c.Gain), parts[0], parts[1], parts[2], allowances[i],
                income, social, Euros(c.Contract.Value) - income - social);
        }).ToList();
    }
}
