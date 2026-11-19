using Nid.Banking.Contracts;
using NodaTime;

namespace Nid.Banking.Application.Reconciliation;

/// <summary>
/// Categorises the lines linked before categories existed, from their link (the charge kind is unknown: the item's kind stands in). Written
/// as <c>link</c> by <c>job</c>, so a person's category is never touched; a line whose link maps to nothing stays as it is. Safe to re-run.
/// </summary>
public sealed class CategoryBackfill(ITransactionStore transactions, ILinkStore links, ICategoryStore categories, LinkCategoryReader reader, IClock clock)
{
    /// <summary>How many lines took a category, reading <paramref name="page"/> lines at a time.</summary>
    public async Task<int> RunAsync(CancellationToken ct, int page = 500)
    {
        var done = 0;
        Guid? after = null;
        var resolved = new Dictionary<string, Guid?>();
        while (true)
        {
            var lines = await transactions.UncategorisedLinkedAsync(after, page, ct);
            if (lines.Count == 0)
                return done;

            after = lines[^1].Id;
            var byLine = await links.ForTransactionsAsync(lines.Select(t => t.Id).ToList(), ct);
            var perCategory = new Dictionary<Guid, List<Guid>>();
            foreach (var line in lines)
            {
                if (!byLine.TryGetValue(line.Id, out var link) || await reader.KeyAsync(link, null, line.Amount > 0, ct) is not { } key)
                    continue;
                if (!resolved.TryGetValue(key, out var category))
                    resolved[key] = category = await categories.ResolveKeyAsync(key, ct);
                if (category is { } id)
                    (perCategory.TryGetValue(id, out var ids) ? ids : perCategory[id] = []).Add(line.Id);
            }

            foreach (var (category, ids) in perCategory)
                done += await categories.SetForLinesAsync(ids, category, CategorySource.Link, "job", clock.GetCurrentInstant(), false, ct);
        }
    }
}
