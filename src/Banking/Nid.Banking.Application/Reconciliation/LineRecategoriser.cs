using Nid.Banking.Contracts;

namespace Nid.Banking.Application.Reconciliation;

/// <summary>A line whose category was just cleared takes its link's category when it is linked, then a rule's or a hint's, as the passes would.</summary>
public sealed class LineRecategoriser(ILinkStore links, ICategoryStore categories, LinkCategoryReader reader, CategoryRuleApplier categoryRules)
{
    public async Task ApplyAsync(BankTransaction line, CancellationToken ct)
    {
        // The link does not keep its charge's kind: the item's kind stands in, as for the backfill.
        if ((await links.ForTransactionsAsync([line.Id], ct)).GetValueOrDefault(line.Id) is { } link
            && await reader.KeyAsync(link, null, line.Amount > 0, ct) is { } key
            && await categories.ResolveKeyAsync(key, ct) is { } category)
            await categories.SetForLinesAsync([line.Id], category, CategorySource.Link, link.DecidedBy, link.DecidedAt, byPerson: false, ct);
        await categoryRules.ApplyAsync(line.Id, ct);
    }
}
