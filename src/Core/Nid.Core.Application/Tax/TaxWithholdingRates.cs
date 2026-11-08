using Nid.Core.Contracts;
using Nid.Core.Domain.Tax;
using NodaTime;

namespace Nid.Core.Application.Tax;

/// <summary>Income #408: the withholding rate of the household's estimate against the real rate, for the salary pages.</summary>
public sealed class TaxWithholdingRates(TaxEstimates estimates, ITaxProfileStore store, IPersonRegistry persons) : ITaxWithholdingRates
{
    public async Task<IReadOnlyList<DeclarantWithholdingRates>> RatesAsync(LocalDate today, CancellationToken ct)
    {
        // As the property return reads the marginal rate: without a profile the household does not use the tax estimate.
        if (TaxEstimates.CheckYear(today.Year, today) is not null || await store.FindAsync(today.Year, ct) is null
            || !(await estimates.ComputeAsync(today.Year, VisibilityScope.Household, today, ct)).TryGetValue(out var result)
            || TaxCalculator.RealRate(result) is not { } real)
            return [];
        return Of(result, real, await persons.ListAsync(ct));
    }

    /// <summary>Each declarant with salaries, matched to their person by id (a declarant without a known person is left out).</summary>
    public static IReadOnlyList<DeclarantWithholdingRates> Of(TaxResult result, decimal real, IReadOnlyList<PersonInfo> people)
        => result.Withholding.Declarants.Where(d => d.Salaries > 0m)
            .Select(d => (d, Person: people.FirstOrDefault(p => p.Id.Value == d.Person))).Where(x => x.Person is not null)
            .Select(x => new DeclarantWithholdingRates(x.Person!.Id, x.Person.DisplayName, result.Withholding.Rate, real, TaxCalculator.MonthlyGap(result, x.d)))
            .ToList();
}
