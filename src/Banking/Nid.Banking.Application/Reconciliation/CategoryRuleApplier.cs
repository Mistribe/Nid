using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Banking.Application.Reconciliation;

/// <summary>
/// Spec §Rules: each line with no category, or one from a rule or a hint, takes the category of the rule that wins among those it matches
/// and <c>mayApply</c> allows on its account. A line no rule matches keeps its category: a pass never retracts a rule's.
/// </summary>
public sealed class CategoryRuleApplier(
    ICategoryRuleStore rules, ICategoryStore categories, ITransactionStore transactions, IAccountStore accounts, BankingAccess access, IClock clock)
{
    /// <summary>On one line, as the pass would (its authors write its account): after an unlink or « Laisser Nid décider ».</summary>
    public async Task<int> ApplyAsync(Guid line, CancellationToken ct)
    {
        if (await transactions.FindAsync(line, ct) is not { } found || await accounts.FindAsync(found.Account, ct) is not { } account)
            return 0;
        var writers = await access.ReadersAsync(account, ct, AccessKind.Write);
        return await ApplyAsync([found], (rule, _) => Task.FromResult(writers.Contains(rule.CreatedBy)), ct);
    }

    /// <summary>How many lines took a rule's category; the rules that set one are touched.</summary>
    public async Task<int> ApplyAsync(IReadOnlyList<BankTransaction> lines, Func<CategoryRule, Guid, Task<bool>> mayApply, CancellationToken ct)
    {
        // Precedence in SetForLinesAsync guards hand, assistant and link too; skipping them here keeps a rule from being touched for nothing.
        // An ignored line is set aside by a person: « Ignorer » clears a rule's category, no rule brings one back.
        var open = lines.Where(l => l.LinkState != TransactionLinkState.Ignored && l.CategorySource is (null or CategorySource.Rule or CategorySource.Hint)).ToList();
        if (open.Count == 0)
            return 0;
        var all = await rules.AllAsync(ct);
        if (all.Count == 0)
            return 0;
        // An archived category is no rule target (spec §Category); a merged one's rules moved to its target.
        var archived = (await categories.AllAsync(ct)).Where(c => c.ArchivedAt is not null).Select(c => c.Id).ToHashSet();
        var live = all.Where(r => !archived.Contains(r.Category)).ToList();

        var allowed = new Dictionary<(Guid Rule, Guid Account), bool>();
        var picks = new List<(CategoryRule Rule, Guid Line)>();
        foreach (var line in open)
        {
            var counterparty = line.Counterparty is { } raw ? TransactionLabel.Normalize(raw) : null;
            var usable = new List<CategoryRule>();
            foreach (var rule in live.Where(r => CategoryRules.Matches(r, line.Account, line.Amount, line.NormalizedLabel, counterparty)))
                if (allowed.TryGetValue((rule.Id, line.Account), out var may) ? may : allowed[(rule.Id, line.Account)] = await mayApply(rule, line.Account))
                    usable.Add(rule);
            if (CategoryRules.Pick(usable, line.Account, line.Amount, line.NormalizedLabel, counterparty) is { } winner)
                picks.Add((winner, line.Id));
        }

        var now = clock.GetCurrentInstant();
        var applied = 0;
        var used = new List<Guid>();
        foreach (var group in picks.GroupBy(p => p.Rule))
        {
            var changed = await categories.SetForLinesAsync(
                [.. group.Select(p => p.Line)], group.Key.Category, CategorySource.Rule, $"rule:{group.Key.Id:D}", now, byPerson: false, ct, group.Key.Id);
            applied += changed;
            if (changed > 0)
                used.Add(group.Key.Id);
        }
        if (used.Count > 0)
            await rules.TouchAsync(used, now, ct);
        return applied;
    }
}
