using Nid.Childcare.Domain;
using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Childcare.Application;

/// <summary>
/// Childcare decision 20: the current month's cost of each arrangement running this month — frozen once validated, else computed from its
/// days with the CMG entered or estimated (decision 19), and the last validated cost when the month cannot be computed.
/// </summary>
public sealed class ChildcareCommitments(IVisibleItems visible, IChildcareStore store, StatementAssembly assembly, IHouseholdSettings household)
    : ICommitmentsContribution
{
    public async Task<Result<CommitmentsPart>> CommitmentsAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct)
    {
        var currency = await household.ReferenceCurrencyAsync(ct);
        var month = today.ToYearMonth();
        var total = 0m;
        var count = 0;
        var estimated = false;
        // Closed ones too: closing archives at once, while the month it ends in still runs and is paid.
        foreach (var arrangement in await store.FindManyAsync((await visible.ForAsync(reader, scope, includeArchived: true, ct)).ToList(), ct))
        {
            // A month outside the arrangement's (ended before it, or before the rules) counts nothing.
            if (!(await assembly.ContextAsync(arrangement.ItemId, month, ct)).TryGetValue(out var context))
                continue;
            var validated = context.Stored.Where(s => s is { Validated: true, Figures: not null }).OrderBy(s => s.Month).ToList();
            decimal? cost;
            if (context.StoredMonth is { Validated: true, Figures: { } frozen })
                cost = frozen.Cost;
            else
            {
                var (days, stale) = await assembly.DaysAsync(context, ct);
                var cmg = context.StoredMonth?.Cmg ?? StatementContext.CmgEstimate(validated.Select(s => (s.Month, s.Figures!.Cmg)).ToList()) ?? 0m;
                var evaluation = await assembly.EvaluateAsync(context, days, stale, context.StoredMonth?.Mileage ?? 0m, cmg, ct);
                cost = evaluation.Figures?.Cost ?? (validated.Count > 0 ? validated[^1].Figures!.Cost : null);
                estimated |= cost is not null;
            }
            if (cost is not { } c)
                continue;
            total += c;
            count++;
        }
        IReadOnlyDictionary<Currency, decimal> amounts = count == 0 ? new Dictionary<Currency, decimal>() : new Dictionary<Currency, decimal> { [currency] = total };
        return Result.Success(new CommitmentsPart(CommitmentSources.Childcare, count, amounts, amounts, estimated));
    }
}
