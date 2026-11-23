using Nid.Banking.Application.Reconciliation;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using NodaTime;

namespace Nid.Banking.Application.Spending;

/// <summary>
/// #85 decision 7: what the household's expected debits of a month commit, per category, in the reference currency; a child's amount also
/// counts in its parent's. A charge maps as a line linked to it would (<see cref="LinkCategory.KeyFor"/>); one without a rate is left out,
/// and <c>Incomplete</c> says so. <c>ToDate</c>: the part due on or before today (the pace counts a dated charge whole from its day).
/// </summary>
public sealed class CommittedByCategory(
    BankingAccess access, ExpectedChargeReader charges, ISubscriptionLabels labels, ICategoryStore categories, IFxRates fx)
{
    public async Task<(Dictionary<Guid, (decimal Month, decimal ToDate)> Amounts, bool Incomplete)> ForAsync(
        YearMonth month, Currency reference, LocalDate today, IReadOnlyDictionary<Guid, BankCategory> byId, CancellationToken ct)
    {
        var days = month.ToDateInterval();
        var due = (await charges.InScopeAsync(access.Person, VisibilityScope.Household, days.Start, days.End, ct)).Where(c => !c.Credit).ToList();
        var committed = new Dictionary<Guid, (decimal Month, decimal ToDate)>();
        if (due.Count == 0)
            return (committed, false);

        var subscriptions = due.Where(c => c.Kind.Name == ChargeKinds.Subscription).Select(c => c.Item).Distinct().ToList();
        IReadOnlyDictionary<ItemId, IReadOnlyList<string>> labelKeys = subscriptions.Count == 0
            ? new Dictionary<ItemId, IReadOnlyList<string>>()
            : await labels.LabelKeysAsync(subscriptions, ct);
        var foreign = due.Select(c => c.Amount.Currency).Where(c => c != reference).Distinct().ToList();
        // As the spending's own lines (decision 5): the reference's quote too, the month-end rate, today's for a month not over.
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0)
            : await fx.LoadAsync([.. foreign, reference], days.End < today ? days.End : today, today, ct);

        var resolved = new Dictionary<string, Guid?>();
        var incomplete = false;
        foreach (var charge in due)
        {
            // The charge's kind decides the key, so the item's kind is never needed.
            if (LinkCategory.KeyFor(LinkOutcome.AsExpected, charge.Kind.Name, charge.Module, null, false, labelKeys.GetValueOrDefault(charge.Item) ?? []) is not { } key)
                continue;
            if (!resolved.TryGetValue(key, out var category))
                resolved[key] = category = await categories.ResolveKeyAsync(key, ct);
            if (category is not { } id)
                continue;
            if (!rates.TryConvert(charge.Amount.Amount, charge.Amount.Currency, reference, days.End, projected: true, out var amount, out _))
            {
                incomplete = true;
                continue;
            }
            var toDate = charge.DueOn <= today ? amount : 0m;
            Guid[] counted = byId.GetValueOrDefault(id)?.Parent is { } parent ? [id, parent] : [id];
            foreach (var target in counted)
            {
                var (total, sofar) = committed.GetValueOrDefault(target);
                committed[target] = (total + amount, sofar + toDate);
            }
        }
        return (committed, incomplete);
    }
}
