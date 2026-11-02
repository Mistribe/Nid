using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Childcare.Application;

/// <summary>Childcare decision 21: one childcare tax line per validated statement of the revenue year, its tax base, without a person.</summary>
public sealed class ChildcareTax(IVisibleItems visible, IItemRegistry items, IChildcareStore store) : ITaxContribution
{
    public async Task<Result<IReadOnlyList<TaxLine>>> TaxLinesAsync(PersonId reader, VisibilityScope scope, int year, LocalDate today, CancellationToken ct)
    {
        // Closed arrangements too: their validated months were paid that year.
        var ids = (await visible.ForAsync(reader, scope, includeArchived: true, ct)).ToList();
        var found = await store.FindManyAsync(ids, ct);
        var registered = await items.FindManyAsync(found.Select(a => a.ItemId).ToList(), ct);
        var lines = new List<TaxLine>();
        foreach (var arrangement in found)
        {
            if (!registered.TryGetValue(arrangement.ItemId, out var item))
                continue;
            lines.AddRange((await store.StatementsOfAsync(arrangement.ItemId, ct))
                .Where(s => s.Validated && s.Month.Year == year && s.Figures is not null)
                .Select(s => new TaxLine(TaxLineCodes.ChildcareOutsideHome, arrangement.ItemId, item.DisplayName, null, s.Figures!.TaxBase)));
        }
        return Result.Success<IReadOnlyList<TaxLine>>(lines);
    }
}
