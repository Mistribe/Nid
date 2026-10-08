using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Income.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Income.Application;

/// <summary>
/// 2D1 spec §4: the monthly equivalent of the phases running today, over the reader's visible open sources.
/// A declared source with no running phase still puts an entry (0) in its currency's sum: the aggregation reads
/// « no income declared » as zero sources, so such a source gives 0, not « unknown ».
/// </summary>
public sealed class IncomeSourcesIncome(IVisibleItems visible, IItemRegistry items, ISourceStore sources, IPhaseStore phases) : IIncomeContribution
{
    public async Task<Result<IncomePart>> IncomeAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct)
    {
        var found = await sources.FindManyAsync((await visible.ForAsync(reader, scope, ct)).ToList(), ct);
        var bySource = (await phases.ForSourcesAsync(found.Select(s => s.ItemId).ToList(), ct)).ToLookup(p => p.Source);
        var sums = new Dictionary<Currency, decimal>();
        var count = 0;
        foreach (var source in found)
        {
            var item = await items.FindAsync(source.ItemId, ct);
            if (item is null || item.ArchivedAt is not null)
                continue;
            count++;
            var currency = SourceCheck.CurrencyOf(item);
            sums[currency] = sums.GetValueOrDefault(currency) + IncomeMath.MonthlyEquivalent(bySource[source.ItemId], today);
        }
        return Result.Success(new IncomePart(count, sums));
    }
}
