using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using Nid.Tax.Domain;

namespace Nid.Tax.Application;

public sealed partial class TaxEstimates
{
    /// <summary>The adults who declare, in a stable order: the household's first two.</summary>
    public static IReadOnlyList<PersonInfo> Adults(IReadOnlyList<PersonInfo> people)
        => people.Where(p => p.Kind == PersonKind.Adult).OrderBy(p => p.DisplayName, StringComparer.Ordinal).ThenBy(p => p.Id.Value).Take(2).ToList();

    /// <summary>Rental 2044 spec §4: the estimate's rental figures are the form's columns, also returned for the form.</summary>
    public static (TaxInputs Inputs, IReadOnlyList<TaxNoticeView> Notices, IReadOnlyList<Rental2044Column> Columns) Assemble(
        int year, IReadOnlyList<TaxLine> lines, TaxProfile profile, IReadOnlyList<PersonInfo> people,
        IReadOnlyDictionary<Guid, decimal>? previousYearIncome = null, (decimal N1, decimal N2)? referenceBefore = null)
    {
        var notices = new List<TaxNoticeView>();
        void Notice(string code, TaxLine line) => notices.Add(new TaxNoticeView(code, line.Item, line.Name, Amount(line.Amount)));
        var adults = Adults(people);
        var declarants = new List<TaxDeclarant>();
        foreach (var adult in adults)
        {
            var status = profile.Statuses.TryGetValue(adult.Id.Value, out var wire) && TaxWire.TryStatus(wire, out var s) ? s : EmploymentStatus.NonExecutive;
            var own = lines.Where(l => l.Person == adult.Id).ToList();
            decimal Sum(string code) => own.Where(l => l.Code == code).Sum(l => l.Amount);
            // A net amount (a phase without its gross, a one-off) goes back to its gross at the status's rate.
            decimal? rate = profile.ContributionRates?.TryGetValue(adult.Id.Value, out var r) == true ? r : null;
            decimal? expenses = profile.ActualExpenses?.TryGetValue(adult.Id.Value, out var e) == true ? e : null;
            var unused = profile.PerUnusedCeilings?.GetValueOrDefault(adult.Id.Value) ?? 0m;
            var declarant = new TaxDeclarant(adult.DisplayName, 0m, status, Sum(TaxLineCodes.OtherTaxable), Sum(TaxLineCodes.PerContribution), rate, expenses, unused,
                Sum(TaxLineCodes.SalaryTaxable), PerCeiling: profile.PerCeilings?.TryGetValue(adult.Id.Value, out var ceiling) == true ? ceiling : null,
                PreviousYearIncome: previousYearIncome?.GetValueOrDefault(adult.Id.Value) ?? 0m,
                PerExpiring: profile.PerCeilingsExpiring?.GetValueOrDefault(adult.Id.Value) ?? 0m,
                ExemptIncome: profile.ExemptIncomes?.GetValueOrDefault(adult.Id.Value) ?? 0m, Person: adult.Id.Value, Pensions: Sum(TaxLineCodes.Pension),
                Disabled: profile.DisabledAdults?.Contains(adult.Id.Value) == true,
                // 65 or over on 31 December of the revenue year (CGI art. 157 bis).
                Over65: adult.BirthDate is { } birth && birth.Year <= year - 65);
            // The profile's rate against the payslips': the notice sits on the first salary holding both figures (the adult is no item).
            if (rate is { } declared && ObservedRate.Of(lines, adult.Id) is { } observed && Math.Abs(declared - observed) > 0.03m
                && own.FirstOrDefault(l => l.Code == TaxLineCodes.SalaryRateGross) is { } source)
                notices.Add(new TaxNoticeView(TaxWarnings.ContributionRateDiffers, source.Item, source.Name, Percent(observed)));
            var gross = Sum(TaxLineCodes.SalaryGross) + (Sum(TaxLineCodes.SalaryNet) + Sum(TaxLineCodes.SalaryOneOff)) / (1 - declarant.EffectiveContributionRate);
            declarants.Add(declarant with { SalaryGross = gross });
        }
        if (declarants.Count == 0)
            declarants.Add(new TaxDeclarant("", 0m, EmploymentStatus.NonExecutive, 0m, 0m));

        string[] personal = [TaxLineCodes.SalaryTaxable, TaxLineCodes.SalaryGross, TaxLineCodes.SalaryNet, TaxLineCodes.SalaryOneOff, TaxLineCodes.OtherTaxable, TaxLineCodes.PerContribution, TaxLineCodes.Pension];
        foreach (var line in lines.Where(l => !Informative.Contains(l.Code)))
        {
            if (personal.Contains(line.Code) && !adults.Any(a => a.Id == line.Person) && line.Amount != 0)
                Notice("not_a_declarant", line);
            else if (line.Code == TaxLineCodes.SalaryNet)
                Notice("missing_gross", line);
            else if (line.Code == TaxLineCodes.Unsupported && line.Amount != 0)
                Notice("unsupported_income", line);
            else if (line.Code == TaxLineCodes.Withdrawal)
                Notice("withdrawal_not_estimated", line);
            else if (line.Code is TaxLineCodes.OtherCurrency or TaxLineCodes.RentalChargesRemoved or TaxLineCodes.RentalFeeNotCounted
                     or TaxLineCodes.SavingsInterest or TaxLineCodes.SavingsInterestUnknown or TaxLineCodes.RealisedGain or TaxLineCodes.RealisedGainUnknown)
                Notice(line.Code, line);
        }

        // The works and other expenses the estimate does not count, one notice per property (the 2044 lists them itself).
        foreach (var property in lines.Where(l => l.Code == TaxLineCodes.RentalToCheck).GroupBy(l => (l.Item, l.Name)))
            notices.Add(new TaxNoticeView(TaxLineCodes.RentalToCheck, property.Key.Item, property.Key.Name, Amount(property.Sum(l => l.Amount))));

        // Tax-per spec §4.4: a PER holding a value that received nothing this year, one notice per envelope (not per owner).
        foreach (var envelope in lines.Where(l => l.Code == TaxLineCodes.PerNoContribution && adults.Any(a => a.Id == l.Person)).GroupBy(l => (l.Item, l.Name)))
            notices.Add(new TaxNoticeView("per_without_contributions", envelope.Key.Item, envelope.Key.Name, Amount(0m)));

        decimal All(string code) => lines.Where(l => l.Code == code).Sum(l => l.Amount);
        // Childcare decision 21: the validated statements of the year (#14: and the months still to validate) replace the profile's
        // childcare costs; #411: one notice per arrangement, its total and months, its year summary a link away. Without a validated
        // month, the months are estimated from the contract (childcare_estimated).
        var childcare = lines.Where(l => l.Code is TaxLineCodes.ChildcareOutsideHome or TaxLineCodes.ChildcareProjected).ToList();
        foreach (var arrangement in childcare.GroupBy(l => (l.Item, l.Name)))
        {
            var validated = arrangement.Count(l => l.Code == TaxLineCodes.ChildcareOutsideHome);
            notices.Add(new TaxNoticeView(validated > 0 ? "childcare_from_statements" : "childcare_estimated", arrangement.Key.Item, arrangement.Key.Name,
                Amount(arrangement.Sum(l => l.Amount)), validated, arrangement.Count() - validated));
        }
        // #94: a nanny's tracked months, and the rest of the current year at their mean, replace the profile's home employment.
        var home = lines.Where(l => l.Code is TaxLineCodes.HomeEmploymentTracked or TaxLineCodes.HomeEmploymentProjected).ToList();
        foreach (var nanny in home.GroupBy(l => (l.Item, l.Name)))
            notices.Add(new TaxNoticeView("home_employment_from_months", nanny.Key.Item, nanny.Key.Name, Amount(nanny.Sum(l => l.Amount)),
                nanny.Count(l => l.Code == TaxLineCodes.HomeEmploymentTracked), nanny.Count(l => l.Code == TaxLineCodes.HomeEmploymentProjected)));
        var rentals = lines.Where(l => l.Code == TaxLineCodes.RentalProperty).ToList();
        var pinel = 0m;
        foreach (var commitment in profile.Pinel)
        {
            if (rentals.FirstOrDefault(l => l.Item.Value == commitment.Property) is not { } property)
                continue; // a property out of the scope or no longer rented counts nothing
            var acquired = property.On!.Value.Year;
            pinel += Pinel.AnnualReduction(Pinel.Base(property.Amount, property.Surface), acquired, commitment.Plus,
                commitment.InitialYears, commitment.TotalYears, commitment.StartYear ?? acquired, year);
        }

        var columns = Rental2044.Columns(lines.Where(l => FormLines.Contains(l.Code)).GroupBy(l => l.Item).Select(g =>
        {
            decimal L(string code) => g.Where(l => l.Code == code).Sum(l => l.Amount);
            return new Rental2044Input(g.Key.Value, g.First().Name, L(TaxLineCodes.Rental211), L(TaxLineCodes.Rental221), L(TaxLineCodes.Rental222),
                L(TaxLineCodes.Rental223), L(TaxLineCodes.Rental224), L(TaxLineCodes.Rental227), L(TaxLineCodes.Rental229), L(TaxLineCodes.Rental250));
        }));

        var dependents = profile.Dependents ?? people.Count(p => p.Kind == PersonKind.Dependent);
        var inputs = new TaxInputs(
            year, declarants, dependents, Math.Min(profile.ChildrenUnder6, dependents),
            columns.Sum(c => c.L215), columns.Sum(c => c.L250), columns.Sum(c => c.L240),
            TaxWire.TryRegime(profile.RentalRegime, out var regime) ? regime : null,
            All(TaxLineCodes.CapitalIncome) + All(TaxLineCodes.SavingsInterest), All(TaxLineCodes.NonTaxable), pinel,
            childcare.Count > 0 ? childcare.Sum(l => l.Amount) : profile.ChildcareOutsideHome, home.Count > 0 ? home.Sum(l => l.Amount) : profile.HomeEmployment, profile.Donations, profile.DonationsAid, profile.RentalDeficitCarried, profile.PerPooling,
            profile.DonationsAidLate, profile.RentalEnergyWorks,
            All(TaxLineCodes.CapitalDividends), profile.CapitalScale, profile.DeductibleCsg, referenceBefore,
            profile.AlimonyPaid, profile.AlimonyAdultChildren, profile.AlimonyAdultChildrenCount,
            All(TaxLineCodes.RealisedGain), profile.CapitalLossesCarried,
            // Box T: a single declarant is taken to live alone with the children unless the profile says otherwise.
            AlternateCustody: Math.Min(profile.AlternateCustody, dependents), SingleParent: adults.Count == 1 && !profile.Cohabiting,
            DisabledDependents: Math.Min(profile.DisabledDependents, dependents),
            AlternateUnder6: Math.Min(profile.AlternateUnder6, Math.Min(Math.Min(profile.ChildrenUnder6, profile.AlternateCustody), dependents)));
        return (inputs, notices, columns);
    }

    /// <summary>Lines shown, never summed nor noticed: a payment's detail and the figures of the observed rate.</summary>
    private static readonly HashSet<string> Informative = [TaxLineCodes.SalaryDetail, TaxLineCodes.SalaryRateGross, TaxLineCodes.SalaryRateTaxable];

    private static readonly HashSet<string> FormLines =
    [
        TaxLineCodes.Rental211, TaxLineCodes.Rental221, TaxLineCodes.Rental222, TaxLineCodes.Rental223,
        TaxLineCodes.Rental224, TaxLineCodes.Rental227, TaxLineCodes.Rental229, TaxLineCodes.Rental250,
    ];
}
