using Nid.Childcare.Domain;
using Nid.Core.Contracts;
using Nid.Tax.Contracts;
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
        var statements = (await store.StatementsOfAsync(found.Select(a => a.ItemId).ToList(), ct)).ToLookup(s => s.ItemId); // #644: batched reads
        var kept = found.Where(a => registered.ContainsKey(a.ItemId))
            .Select(a => (Arrangement: a, Revisions: terms[a.ItemId].OrderBy(t => t.From).ToList(), Statements: statements[a.ItemId].ToList())).ToList();
        var estimates = await EstimatesAsync(kept, year, today, ct);
        var lines = new List<(PersonId Child, TaxLine Line)>();
        foreach (var (arrangement, revisions, stored) in kept)
        {
            var item = registered[arrangement.ItemId];
            // #411: the year summary sums the same statements (ChildcareYear.Counted).
            lines.AddRange(ChildcareYear.Counted(stored, year)
                .Select(s => (arrangement.Child, new TaxLine(TaxLineCodes.ChildcareOutsideHome, arrangement.ItemId, item.DisplayName, null, s.Figures!.TaxBase))));
            lines.AddRange(ChildcareYear.Projected(revisions[0].From, revisions[^1].To, today, stored, year, estimates.GetValueOrDefault(arrangement.ItemId))
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

    /// <summary>
    /// No validated month in the current year: the latest month begun so far computed from the contract, as the dashboard does. The contexts
    /// are read at once (#644); each estimate still reads the stored months it needs.
    /// </summary>
    private async Task<Dictionary<ItemId, decimal?>> EstimatesAsync(
        IReadOnlyList<(Arrangement Arrangement, List<Terms> Revisions, List<Statement> Statements)> kept, int year, LocalDate today, CancellationToken ct)
    {
        var estimates = new Dictionary<ItemId, decimal?>();
        if (year != today.Year)
            return estimates;
        var wanted = new List<(Arrangement, YearMonth)>();
        foreach (var (arrangement, revisions, statements) in kept)
        {
            if (ChildcareYear.Counted(statements, year).Count > 0)
                continue;
            var months = ChildcareYear.NotValidated(revisions[0].From, revisions[^1].To, today, statements, year);
            if (months.Count > 0)
                wanted.Add((arrangement, months[^1]));
        }
        foreach (var (id, context) in await assembly.ContextsAsync(wanted, ct))
            estimates[id] = (await assembly.EstimateAsync(context, ct)).Figures?.TaxBase;
        return estimates;
    }
}
