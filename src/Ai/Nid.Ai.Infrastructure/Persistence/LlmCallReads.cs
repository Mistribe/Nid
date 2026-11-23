using Microsoft.EntityFrameworkCore;
using Nid.Ai.Application;
using NodaTime;

namespace Nid.Ai.Infrastructure.Persistence;

/// <summary>The reads of <c>llm_call</c> for « Utilisation IA »: the page and the totals are computed in SQL.</summary>
public sealed class LlmCallReads(AiDbContext db) : ILlmCallReads
{
    public async Task<(IReadOnlyList<LlmCallItem> Items, int Total)> PageAsync(Guid person, int skip, int take, CancellationToken ct)
    {
        var own = db.LlmCalls.AsNoTracking().Where(c => c.PersonId == person);
        var total = await own.CountAsync(ct);
        var items = await own.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Skip(skip).Take(take)
            .Select(c => new LlmCallItem(c.Id, c.CreatedAt, c.Use, c.Model, c.Provider, c.WorkerId, c.TokensIn, c.TokensOut, c.CostEstimateEur,
                c.Status, c.Code))
            .ToListAsync(ct);
        return (items, total);
    }

    public async Task<LlmMonthTotals> MonthAsync(Instant from, Instant to, CancellationToken ct)
    {
        var month = db.LlmCalls.AsNoTracking().Where(c => c.CreatedAt >= from && c.CreatedAt < to);
        var totals = await month.GroupBy(_ => 1)
            .Select(g => new { Calls = g.Count(), In = g.Sum(c => (long)c.TokensIn), Out = g.Sum(c => (long)c.TokensOut), Cost = g.Sum(c => c.CostEstimateEur ?? 0) })
            .SingleOrDefaultAsync(ct);
        return totals is null ? new LlmMonthTotals(0, 0, 0, 0) : new LlmMonthTotals(totals.Calls, totals.In, totals.Out, totals.Cost);
    }
}
