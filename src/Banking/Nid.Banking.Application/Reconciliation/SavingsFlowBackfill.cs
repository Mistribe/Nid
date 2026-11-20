using Nid.Banking.Contracts;
using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Banking.Application.Reconciliation;

/// <summary>
/// #16, once per startup: the lines of savings accounts booked before « Intérêts » existed take its hint, then every savings account's lines
/// become its envelope's flows (<see cref="SavingsFlows"/>). Safe to re-run: a second run hints and writes nothing.
/// </summary>
public sealed class SavingsFlowBackfill(
    IAccountStore accounts, ITransactionStore transactions, IEnvelopeStore envelopes, CategoryRuleApplier categoryRules, SavingsFlows savingsFlows)
{
    private const int Page = 500;

    public async Task<SavingsFlowCounts> RunAsync(CancellationToken ct)
    {
        var associated = (await accounts.AllAsync(ct)).Where(a => a is { Envelope: not null, AssociatedOn: not null }).ToList();
        var savings = (await envelopes.FindManyAsync([.. associated.Select(a => a.Envelope!.Value).Distinct()], ct))
            .Where(e => e.Kind == EnvelopeKind.Savings).Select(e => e.ItemId).ToHashSet();
        foreach (var account in associated.Where(a => savings.Contains(a.Envelope!.Value)))
        {
            // The pass reads from the association's month: the hint only matters there.
            var from = account.AssociatedOn!.Value.With(DateAdjusters.StartOfMonth);
            IReadOnlyList<BankTransaction> page;
            var skip = 0;
            do
            {
                page = await transactions.ListAsync([account.Id], from, null, skip, Page, ct);
                await categoryRules.HintAsync(page, ct);
                skip += page.Count;
            } while (page.Count == Page);
        }
        return await savingsFlows.RunAsync(null, null, ct);
    }
}
