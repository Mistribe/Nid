using Nid.Core.Contracts;

namespace Nid.Core.Domain.Tax;

/// <summary>
/// Issue #410: one calculation line that moved since last year. <c>Before</c> is last year's (the notice's figure when <c>FromNotice</c>,
/// else last year's estimate), <c>Now</c> this year's estimate; <c>Effect</c> what the change alone does to this year's tax, in euros
/// (positive: more tax). <c>Person</c> is the declarant of a salary line.
/// </summary>
public sealed record TaxChangeLine(string Code, Guid? Person, decimal Before, decimal Now, decimal Effect, bool FromNotice);

/// <summary>
/// Issue #410: the lines sorted by the size of their effect, the tax of both years (income tax and flat tax, as the notice's « impôt
/// net ») and <c>Remainder</c>, what the lines do not explain (the year's scale, the caps, the rounding): the change of the tax minus
/// the effects. <c>TaxBefore</c> and <c>Remainder</c> are null when last year has no total.
/// </summary>
public sealed record TaxChangesResult(decimal? TaxBefore, bool TaxBeforeFromNotice, decimal TaxNow, decimal? Remainder, IReadOnlyList<TaxChangeLine> Lines);

/// <summary>
/// Issue #410: this year's estimate against last year's, line by line. Each line's effect is indicative: an income or a deduction goes
/// through this year's scale and shares on this year's net taxable income, the shares through the scale with last year's, a
/// reduction or a credit counts as it is.
/// </summary>
public static class TaxChanges
{
    /// <summary>
    /// <paramref name="before"/> is last year's estimate (null before the rules' first year); the first of <paramref name="notices"/>
    /// whose declarants are exactly <paramref name="adults"/> gives the figures it prints. Null without either.
    /// </summary>
    public static TaxChangesResult? Of(TaxResult now, TaxResult? before, IEnumerable<TaxAssessment> notices, IReadOnlyList<Guid> adults)
    {
        var notice = notices.FirstOrDefault(n => n.Declarants.Select(d => d.Person.Value).ToHashSet().SetEquals(adults));
        if (before is null && notice is null)
            return null;

        var rules = TaxRules.For(now.Year);
        var declarants = now.Declarants.Count;
        decimal Gross(decimal taxable, decimal baseParts, decimal parts) => TaxCalculator.GrossTax(rules, Math.Max(0m, taxable), baseParts, parts).Tax;
        var grossNow = Gross(now.NetTaxableIncome, declarants, now.Parts);
        // The tax this year's income would lose without the change: a change of the net taxable income through this year's scale.
        decimal Income(decimal change) => grossNow - Gross(now.NetTaxableIncome - change, declarants, now.Parts);

        var lines = new List<TaxChangeLine>();
        void Add(string code, Guid? person, decimal? printed, decimal? estimated, decimal value, Func<decimal, decimal> effect)
        {
            if ((printed ?? estimated) is not { } prior || prior == value)
                return;
            lines.Add(new TaxChangeLine(code, person, prior, value, Euros(effect(prior)), printed is not null));
        }

        for (var i = 0; i < Math.Min(adults.Count, declarants); i++)
        {
            var printed = notice?.Declarants.First(d => d.Person.Value == adults[i]).NetSalaries;
            Add("salaries", adults[i], printed, before?.Declarants.ElementAtOrDefault(i)?.Taxable, now.Declarants[i].Taxable, p => Income(now.Declarants[i].Taxable - p));
        }
        Add("rental_income", null, notice?.RentalIncomeNet, before?.RentalTaxable, now.RentalTaxable, p => Income(now.RentalTaxable - p));
        Add("rental_deficit", null, notice?.RentalDeficitOnGlobalIncome, before?.RentalDeficit, now.RentalDeficit, p => Income(p - now.RentalDeficit));
        var per = now.Declarants.Sum(d => d.PerDeducted);
        Add("per_deduction", null, null, before?.Declarants.Sum(d => d.PerDeducted), per, p => Income(p - per));
        var baseBefore = notice?.Declarants.Count ?? before!.Declarants.Count;
        Add("shares", null, notice?.Shares, before?.Parts, now.Parts, p => grossNow - Gross(now.NetTaxableIncome, baseBefore, p));
        Add("decote", null, null, before?.Decote, now.Decote, p => p - now.Decote);
        Add("pinel", null, notice?.PinelReduction, before?.PinelReduction, now.PinelReduction, p => p - now.PinelReduction);
        Add("donations", null, null, before?.DonationReduction, now.DonationReduction, p => p - now.DonationReduction);
        Add("childcare", null, null, before?.ChildcareCredit, now.ChildcareCredit, p => p - now.ChildcareCredit);
        Add("home_employment", null, null, before?.HomeEmploymentCredit, now.HomeEmploymentCredit, p => p - now.HomeEmploymentCredit);
        Add("flat_tax", null, notice?.ProportionalTax, before?.CapitalFlatTax, now.CapitalFlatTax, p => now.CapitalFlatTax - p);

        var taxNow = now.IncomeTax + now.CapitalFlatTax;
        var taxBefore = notice?.NetTax ?? (before is { } b ? b.IncomeTax + b.CapitalFlatTax : null);
        return new TaxChangesResult(
            taxBefore, notice?.NetTax is not null, taxNow, taxBefore is { } t ? Euros(taxNow - t - lines.Sum(l => l.Effect)) : null,
            lines.OrderByDescending(l => Math.Abs(l.Effect)).ThenBy(l => l.Code, StringComparer.Ordinal).ToList());
    }

    private static decimal Euros(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}
