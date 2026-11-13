using Nid.Childcare.Domain;
using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Childcare.Application;

/// <summary>
/// Childcare decision 21: one childcare tax line per validated statement of the revenue year, its tax base, without a person; #14: one per
/// month left of the current year, estimated.
/// </summary>
public sealed class ChildcareTax(IVisibleItems visible, IItemRegistry items, IChildcareStore store, StatementAssembly assembly) : ITaxContribution
{
    public async Task<Result<IReadOnlyList<TaxLine>>> TaxLinesAsync(PersonId reader, VisibilityScope scope, int year, LocalDate today, CancellationToken ct)
    {
        // Closed arrangements too: their validated months were paid that year.
        var ids = (await visible.ForAsync(reader, scope, includeArchived: true, ct)).ToList();
        var found = await store.FindManyAsync(ids, ct);
        var registered = await items.FindManyAsync(found.Select(a => a.ItemId).ToList(), ct);
        var terms = (await store.TermsOfAsync(found.Select(a => a.ItemId).ToList(), ct)).ToLookup(t => t.ItemId);
        var lines = new List<TaxLine>();
        foreach (var arrangement in found)
        {
            if (!registered.TryGetValue(arrangement.ItemId, out var item))
                continue;
            var statements = await store.StatementsOfAsync(arrangement.ItemId, ct);
            // #411: the year summary sums the same statements (ChildcareYear.Counted).
            lines.AddRange(ChildcareYear.Counted(statements, year)
                .Select(s => new TaxLine(TaxLineCodes.ChildcareOutsideHome, arrangement.ItemId, item.DisplayName, null, s.Figures!.TaxBase)));
            var revisions = terms[arrangement.ItemId].OrderBy(t => t.From).ToList();
            var estimate = await EstimateAsync(arrangement.ItemId, revisions, statements, year, today, ct);
            lines.AddRange(ChildcareYear.Projected(revisions[0].From, revisions[^1].To, today, statements, year, estimate)
                .Select(m => new TaxLine(TaxLineCodes.ChildcareProjected, arrangement.ItemId, item.DisplayName, null, m.Amount)));
        }
        return Result.Success<IReadOnlyList<TaxLine>>(lines);
    }

    /// <summary>No validated month in the current year: the latest month begun so far computed from the contract, as the dashboard does.</summary>
    private async Task<decimal?> EstimateAsync(ItemId id, IReadOnlyList<Terms> revisions, IReadOnlyList<Statement> statements, int year, LocalDate today, CancellationToken ct)
    {
        if (year != today.Year || ChildcareYear.Counted(statements, year).Count > 0)
            return null;
        var months = ChildcareYear.NotValidated(revisions[0].From, revisions[^1].To, today, statements, year);
        if (months.Count == 0 || !(await assembly.ContextAsync(id, months[^1], ct)).TryGetValue(out var context))
            return null;
        return (await assembly.EstimateAsync(context, ct)).Figures?.TaxBase;
    }
}
