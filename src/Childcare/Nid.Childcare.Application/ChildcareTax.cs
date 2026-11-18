using Nid.Childcare.Domain;
using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Childcare.Application;

/// <summary>
/// Childcare decision 21: one childcare tax line per validated statement of the revenue year, its tax base, without a person; #14: one per
/// month left of the current year, estimated. #233: <c>Category</c> is the 2042 box the line goes in (7GA, 7GB, 7GC; null past the third child).
/// </summary>
public sealed class ChildcareTax(IVisibleItems visible, IItemRegistry items, IChildcareStore store, StatementAssembly assembly, IPersonRegistry persons, INannyStore nannies) : ITaxContribution
{
    public async Task<Result<IReadOnlyList<TaxLine>>> TaxLinesAsync(PersonId reader, VisibilityScope scope, int year, LocalDate today, CancellationToken ct)
    {
        // Closed arrangements too: their validated months were paid that year.
        var ids = (await visible.ForAsync(reader, scope, includeArchived: true, ct)).ToList();
        var found = await store.FindManyAsync(ids, ct);
        var registered = await items.FindManyAsync(found.Select(a => a.ItemId).ToList(), ct);
        var terms = (await store.TermsOfAsync(found.Select(a => a.ItemId).ToList(), ct)).ToLookup(t => t.ItemId);
        var lines = new List<(PersonId Child, TaxLine Line)>();
        foreach (var arrangement in found)
        {
            if (!registered.TryGetValue(arrangement.ItemId, out var item))
                continue;
            var statements = await store.StatementsOfAsync(arrangement.ItemId, ct);
            // #411: the year summary sums the same statements (ChildcareYear.Counted).
            lines.AddRange(ChildcareYear.Counted(statements, year)
                .Select(s => (arrangement.Child, new TaxLine(TaxLineCodes.ChildcareOutsideHome, arrangement.ItemId, item.DisplayName, null, s.Figures!.TaxBase))));
            var revisions = terms[arrangement.ItemId].OrderBy(t => t.From).ToList();
            var estimate = await EstimateAsync(arrangement.ItemId, revisions, statements, year, today, ct);
            lines.AddRange(ChildcareYear.Projected(revisions[0].From, revisions[^1].To, today, statements, year, estimate)
                .Select(m => (arrangement.Child, new TaxLine(TaxLineCodes.ChildcareProjected, arrangement.ItemId, item.DisplayName, null, m.Amount))));
        }
        // #233: the box ranks the child among the children with costs that year, as the year summary does.
        var people = (await persons.ListAsync(ct)).ToDictionary(p => p.Id);
        var children = lines.Select(l => l.Child).Distinct()
            .Select(c => (c, people.GetValueOrDefault(c)?.BirthDate, people.GetValueOrDefault(c)?.DisplayName ?? "")).ToList();
        var result = lines.Select(l => l.Line with { Category = ChildcareYear.Box(l.Child, children) }).ToList();
        result.AddRange(await NannyLinesAsync(ids, year, today, ct));
        return Result.Success<IReadOnlyList<TaxLine>>(result);
    }

    /// <summary>#94: each nanny's recorded months of the year at their net cost, then the rest of the current year at their mean.</summary>
    private async Task<IEnumerable<TaxLine>> NannyLinesAsync(IReadOnlyCollection<ItemId> ids, int year, LocalDate today, CancellationToken ct)
    {
        var found = await nannies.FindManyAsync(ids, ct);
        var heads = await items.FindManyAsync(found, ct);
        var months = (await nannies.MonthsOfAsync(found, ct)).ToLookup(m => m.ItemId);
        return heads.Values.SelectMany(item => NannyYear.Of(months[item.Id], year)
            .Select(m => new TaxLine(TaxLineCodes.HomeEmploymentTracked, item.Id, item.DisplayName, null, m.NetCost))
            .Concat(NannyYear.Projected(months[item.Id], year, today, item.ArchivedAt is not null)
                .Select(m => new TaxLine(TaxLineCodes.HomeEmploymentProjected, item.Id, item.DisplayName, null, m.Amount))));
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
