namespace Nid.Tax.Domain;

/// <summary>Issue #233: a 2042 box to fill, in whole euros; <c>Person</c> the declarant it belongs to, null for the household's.</summary>
public sealed record Tax2042Box(string Code, Guid? Person, decimal Amount);

/// <summary>
/// Issue #233: the 2042 boxes the estimate fills, from its own inputs and result, so the two never differ. The dividends
/// (<see cref="TaxInputs.CapitalDividends"/>) go in 2DC, the rest of the capital income in 2TR; <c>Childcare</c> the childcare costs by box
/// (7GA, 7GB, 7GC); <c>Rental</c> the 2044's totals, whose boxes the real regime copies. Boxes at zero are left out.
/// </summary>
public static class Tax2042
{
    public static IReadOnlyList<Tax2042Box> Boxes(
        TaxInputs inputs, TaxResult result, Rental2044Totals rental, IReadOnlyList<(string Box, decimal Amount)> childcare)
    {
        var boxes = new List<Tax2042Box>();
        void Add(string code, decimal amount, Guid? person = null)
        {
            var euros = Math.Round(amount, 0, MidpointRounding.AwayFromZero);
            if (euros != 0m) boxes.Add(new Tax2042Box(code, person, euros));
        }

        for (var i = 0; i < inputs.Declarants.Count; i++)
        {
            var (d, r, letter) = (inputs.Declarants[i], result.Declarants[i], i == 0 ? 'A' : 'B');
            // The net taxable income without the unemployment, declared apart.
            Add($"1{letter}J", r.NetTaxableIncome - d.OtherTaxable, r.Person);
            Add($"1{letter}K", d.ActualExpenses ?? 0m, r.Person);
            Add($"1{letter}P", d.OtherTaxable, r.Person);
        }

        var capital = Math.Max(0m, inputs.CapitalIncome);
        var dividendsPart = Math.Min(Math.Max(0m, inputs.CapitalDividends), capital);
        Add("2DC", dividendsPart);
        Add("2TR", capital - dividendsPart);
        // The 12.8 % the bank withheld when paying, a credit whichever option (flat tax or scale) the household takes.
        Add("2CK", TaxRules.For(inputs.Year).FlatTaxRate * capital);

        if (result.RentalRegime == RentalRegime.Micro)
            Add("4BE", inputs.RentalGross);
        else
        {
            Add("4BA", rental.Taxable);
            Add("4BB", rental.CarriedDeficit);
            Add("4BC", rental.GlobalDeficit);
        }
        Add("4BD", inputs.RentalDeficitCarried);

        for (var i = 0; i < inputs.Declarants.Count; i++)
            Add(i == 0 ? "6NS" : "6NT", inputs.Declarants[i].PerContributions, result.Declarants[i].Person);

        foreach (var (box, amount) in childcare.GroupBy(c => c.Box).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (g.Key, g.Sum(c => c.Amount))))
            Add(box, amount);
        Add("7DB", inputs.HomeEmployment);
        Add("7UD", inputs.DonationsAid);
        Add("7UQ", inputs.DonationsAidLate);
        Add("7UF", inputs.Donations);
        return boxes;
    }
}
