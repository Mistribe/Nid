using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Nid.Banking.Application.Sync;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Banking.Application.Reconciliation;

/// <summary>What one pass did: lines made internal, lines linked by a rule, link proposals submitted.</summary>
public sealed record ReconciliationCounts(int Internal, int Linked, int Proposed);

/// <summary>
/// 5C spec §6.3: the lines no pass has looked at yet, after a sync or a statement import. Transfers and card settlements first, over the
/// whole batch; then, line by line, its writers' rules, else its single candidate charge, proposed to a writer of the account who writes it (D22). The
/// counts and codes of a failure are logged, never a label or an amount; a failed line stays for the next pass.
/// </summary>
public sealed class BankReconciliation(
    BankingAccess access, ITransactionStore transactions, IAccountStore accountStore, ILinkStore links, IRuleStore rules,
    ExpectedChargeReader charges, TransactionLinker linker, IAccessPolicy policy, IProposalSubmitter proposals, IActorAccessor actor,
    IUnitOfWork unitOfWork, IClock clock, ILogger<BankReconciliation> log)
{
    /// <summary>The widest tolerance of a charge (a rent's): the charges read around the batch.</summary>
    private const int ChargeMarginDays = 10;

    private static readonly ReconciliationCounts None = new(0, 0, 0);

    /// <summary>Steps 1–4 of §6.3 on the unreconciled lines (all accounts when null), then marks them reconciled.</summary>
    public async Task<ReconciliationCounts> RunAsync(IReadOnlyCollection<Guid>? accounts, LocalDate today, CancellationToken ct)
    {
        try
        {
            return (await access.EnabledAsync(ct)).IsSuccess ? await PassAsync(accounts, today, ct) : None;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Never fails the sync or the import: the lines stay unreconciled for the next pass.
            log.LogError("The reconciliation pass threw {Exception}", e.GetType().Name);
            return None;
        }
    }

    private async Task<ReconciliationCounts> PassAsync(IReadOnlyCollection<Guid>? accountIds, LocalDate today, CancellationToken ct)
    {
        var lines = await transactions.UnreconciledAsync(accountIds, ct); // oldest first
        if (lines.Count == 0)
            return None;
        var accounts = (await accountStore.AllAsync(ct)).ToDictionary(a => a.Id);
        var (min, max) = (lines[0].BookedOn, lines[^1].BookedOn);
        var open = lines.Where(l => l.LinkState == TransactionLinkState.Unlinked).Select(l => l.Id).ToHashSet();
        var failed = new HashSet<Guid>();
        var now = clock.GetCurrentInstant();
        int internals = 0, linked = 0, proposed = 0;

        // A line whose work throws is left for the next pass; the others go on.
        async Task TryAsync(IReadOnlyList<Guid> ids, Func<Task> work)
        {
            try
            {
                await work();
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogError("Reconciling a bank line threw {Exception}", e.GetType().Name);
                failed.UnionWith(ids);
                open.ExceptWith(ids);
            }
        }

        Task InternalAsync(Guid line, Guid counterpart, LinkOutcome outcome)
            => links.SetAsync(new BankLink(line, null, null, null, counterpart, outcome, null, "job", now), TransactionLinkState.Internal, ct);

        // Step 1: a counterpart reconciled in an earlier pass still pairs; a pair of two older lines is not this pass's. A card line is never
        // one side of a transfer, and two accounts pair only when an adult reads both: one who sees the pair and can undo it.
        var pool = (await transactions.UnlinkedBetweenAsync(min.PlusDays(-InternalTransfers.Days), max.PlusDays(InternalTransfers.Days), ct))
            .Where(l => accounts.GetValueOrDefault(l.Account) is { Kind: not BankAccountKind.Card, CardOf: null }).ToList();
        var accountOf = pool.ToDictionary(l => l.Id, l => l.Account);
        var poolReaders = new Dictionary<Guid, IReadOnlyList<PersonId>>();
        foreach (var account in accountOf.Values.Distinct())
            poolReaders[account] = await access.ReadersAsync(accounts[account], ct);
        foreach (var (debit, credit) in InternalTransfers.Pair([.. pool.Select(TransactionLinker.LineOf)], (a, b) => poolReaders[a].Intersect(poolReaders[b]).Any()))
            if (open.Contains(debit) || open.Contains(credit))
                await TryAsync([debit, credit], async () =>
                {
                    // Both or neither: a half pair would leave its other line for a pairing that can no longer happen.
                    await using (var both = await unitOfWork.BeginAsync(ct))
                    {
                        await InternalAsync(debit, accountOf[credit], LinkOutcome.InternalTransfer);
                        await InternalAsync(credit, accountOf[debit], LinkOutcome.InternalTransfer);
                        await both.CommitAsync(ct);
                    }
                    open.ExceptWith([debit, credit]);
                    internals += 2;
                });

        // Step 2: a checking debit that pays one attached card's lines since that card's previous settlement.
        var cardsOf = accounts.Values.Where(a => a.CardOf is not null).ToLookup(a => a.CardOf!.Value);
        var debits = lines.Where(l => open.Contains(l.Id) && l.Amount < 0
            && accounts.GetValueOrDefault(l.Account)?.Kind == BankAccountKind.Checking && cardsOf.Contains(l.Account)).ToList();
        List<(Guid? Card, LocalDate BookedOn)> settlements = [];
        // Unread, the previous settlements would give wrong windows: step 2 waits for the next pass, its debits with it.
        if (debits.Count > 0)
            await TryAsync([.. debits.Select(d => d.Id)], async () => settlements =
                [.. (await links.OutcomeSinceAsync(LinkOutcome.CardSettlement, min.PlusYears(-1), ct)).Select(s => (s.Link.CounterpartAccount, s.Line.BookedOn))]);
        foreach (var debit in debits.Where(d => open.Contains(d.Id)))
            await TryAsync([debit.Id], async () =>
            {
                foreach (var card in cardsOf[debit.Account])
                {
                    var since = settlements.Where(s => s.Card == card.Id && s.BookedOn < debit.BookedOn).Select(s => (LocalDate?)s.BookedOn).Max()
                        ?? debit.BookedOn.PlusDays(-CardSettlement.FirstWindowDays);
                    var cardLines = await transactions.ListAsync([card.Id], since, debit.BookedOn, 0, int.MaxValue, ct);
                    if (!CardSettlement.Settles(TransactionLinker.LineOf(debit), [.. cardLines.Select(TransactionLinker.LineOf)], since))
                        continue;
                    await InternalAsync(debit.Id, card.Id, LinkOutcome.CardSettlement);
                    open.Remove(debit.Id);
                    internals++;
                    settlements.Add((card.Id, debit.BookedOn));
                    return;
                }
            });

        // Steps 3 and 4, per account: its writers (who links a line writes its account), their rules that still apply, their writable
        // charges (the first writer's wins) of the items whose readers all read the account (§0 n°1: a proposal shows the line to them).
        var writersOf = new Dictionary<Guid, IReadOnlyList<PersonId>>();
        foreach (var account in lines.Where(l => open.Contains(l.Id)).Select(l => l.Account).Distinct())
            writersOf[account] = accounts.TryGetValue(account, out var found) ? await access.ReadersAsync(found, ct, AccessKind.Write) : [];
        var writable = new Dictionary<PersonId, IReadOnlyList<ExpectedCharge>>();
        foreach (var person in writersOf.Values.SelectMany(r => r).Distinct())
            writable[person] = await charges.WritableAsync(person, min.PlusDays(-ChargeMarginDays), max.PlusDays(ChargeMarginDays), ct);
        var convert = await charges.ConverterAsync([.. writable.Values.SelectMany(c => c)], min, max, ct);
        var rulesOf = new Dictionary<Guid, List<BankRule>>();
        var offeredOf = new Dictionary<Guid, List<(ExpectedCharge Charge, PersonId Depositor)>>();
        foreach (var (account, writers) in writersOf)
        {
            var kept = new List<BankRule>();
            foreach (var rule in await rules.ForAccountAsync(account, ct))
                if (writers.Contains(rule.CreatedBy) && (await policy.CheckAsync(rule.CreatedBy, rule.TargetItem, AccessKind.Write, ct)).IsSuccess)
                    kept.Add(rule);
            rulesOf[account] = kept;
            var seen = new HashSet<(string, ItemId, string)>();
            var told = new Dictionary<ItemId, bool>();
            var offered = new List<(ExpectedCharge, PersonId)>();
            foreach (var (charge, writer) in writers.SelectMany(w => writable[w].Select(c => (c, w))))
                if (seen.Add((charge.Module, charge.Item, charge.Key))
                    && (told.TryGetValue(charge.Item, out var all) ? all : told[charge.Item] = await access.ItemReadersReadAsync(account, charge.Item, ct)))
                    offered.Add((charge, writer));
            offeredOf[account] = offered;
        }

        var candidates = new List<(BankTransaction Line, ExpectedCharge Charge, PersonId Depositor)>();
        foreach (var line in lines.Where(l => open.Contains(l.Id)))
            await TryAsync([line.Id], async () =>
            {
                var stem = TransactionLabel.Stem(line.NormalizedLabel);
                List<BankRule> applicable = stem.Length == 0 ? [] : rulesOf[line.Account].Where(r => r.LabelStem == stem).ToList();
                if (applicable.Count > 0)
                {
                    // Two targets, or no charge within tolerance: the line stays unlinked, without a guess.
                    if (applicable.Select(r => r.TargetItem).Distinct().Count() == 1 && await linker.ApplyRuleAsync(line, applicable[0], today, ct))
                        linked++;
                    return;
                }
                if (line.BookedOn < today.PlusDays(-TransactionLinker.AskDays))
                    return;
                var offered = offeredOf[line.Account];
                var dues = offered.Select((o, i) => TransactionLinker.DueOf(o.Charge, i)).ToList();
                if (ChargeMatch.SingleCandidate(TransactionLinker.LineOf(line), dues, d => convert(offered[d.Index].Charge, line.BookedOn, line.Currency.Code)) is { } index)
                    candidates.Add((line, offered[index].Charge, offered[index].Depositor));
            });

        // A long first read asks once per item: its most recent line.
        foreach (var (line, charge, depositor) in candidates.GroupBy(c => c.Charge.Item).Select(g => g.MaxBy(c => c.Line.BookedOn)))
            await TryAsync([line.Id], async () =>
            {
                if (await ProposeAsync(line, charge, depositor, ct))
                    proposed++;
            });

        await transactions.MarkReconciledAsync([.. lines.Select(l => l.Id).Where(id => !failed.Contains(id))], now, ct);
        return new ReconciliationCounts(internals, linked, proposed);
    }

    /// <summary>A <c>banking.link_transaction</c> proposal, submitted as <paramref name="depositor"/>; whether one was created.</summary>
    private async Task<bool> ProposeAsync(BankTransaction line, ExpectedCharge charge, PersonId depositor, CancellationToken ct)
    {
        var payload = TransactionLinker.Element(new JsonObject
        {
            ["transactionId"] = line.Id.ToString("D"), ["module"] = charge.Module, ["itemId"] = charge.Item.Value.ToString("D"), ["key"] = charge.Key,
        });
        var saved = actor.Current;
        actor.Current = Actor.BankSync(depositor);
        try
        {
            var submitted = await proposals.SubmitAsync("banking.link_transaction", charge.Item, payload, BankSyncCollector.SourceLabel, ct);
            if (submitted.TryGetValue(out var created))
                return created;
            log.LogWarning("A bank link proposal was refused {Code}", submitted.TryGetFailure(out var failure) ? failure!.Code : null);
            return false;
        }
        finally
        {
            actor.Current = saved;
        }
    }
}
