using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.Income.Application;
using Nid.Income.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Income.Infrastructure.Persistence;

/// <summary>
/// #23: Income's share of an item delete: the source, its phases and their taxable shares, and the phases' and one-off incomes' document
/// anchors. One-off incomes are Core flows: Core's part counts and removes them.
/// </summary>
public sealed class IncomeDeletionPart(IncomeDbContext db, IFlows flows, IDocumentLinks documents) : IItemDeletionPart
{
    /// <summary>DateRange has no unbounded form; no flow predates this.</summary>
    private static readonly LocalDate Earliest = new(1900, 1, 1);

    private static bool IsSource(ItemAccess item) => item is { Module: "income", Kind: "source" };

    public async Task<IReadOnlyList<DeletionCount>> CountAsync(ItemAccess item, CancellationToken ct)
        => !IsSource(item) ? [] : [new("phases", await db.Phases.CountAsync(p => p.SourceItemId == item.Id.Value, ct))];

    public Task<IReadOnlyList<DeletionBlocker>> BlockersAsync(ItemAccess item, CancellationToken ct) => Task.FromResult<IReadOnlyList<DeletionBlocker>>([]);

    public async Task<bool> ImportedAsync(ItemAccess item, CancellationToken ct)
        => IsSource(item) && await db.Sources.AnyAsync(s => s.ItemId == item.Id.Value && s.ImportRef != null, ct);

    public async Task RemoveAsync(ItemAccess item, CancellationToken ct)
    {
        if (!IsSource(item))
            return;
        var id = item.Id.Value;
        var phases = await db.Phases.Where(p => p.SourceItemId == id).ToListAsync(ct);
        // Dismissals of an expected document go with the target; the links already go with the anchor (Documents' part).
        foreach (var phase in phases)
            await documents.UnlinkTargetAsync(new DocumentTarget(IncomeDocumentTargets.PhaseKind, phase.Id), ct);
        DateRange.Create(Earliest, null).TryGetValue(out var everything);
        var listed = await flows.ListAsync(item.Id, everything, new HashSet<FlowKind> { FlowKind.Income }, ct);
        if (!listed.TryGetValue(out var current))
            throw new InvalidOperationException("The actor may write the source, so its flows are readable.");
        foreach (var flow in current!.Where(f => f.Category == IncomeWire.OneOffCategory))
            await documents.UnlinkTargetAsync(new DocumentTarget(IncomeDocumentTargets.OneOffKind, flow.Id), ct);

        // Tracked removes, children first: the audit sees every row the cascade would have taken silently.
        db.OneOffTaxables.RemoveRange(await db.OneOffTaxables.Where(t => t.SourceId == id).ToListAsync(ct));
        db.Phases.RemoveRange(phases);
        db.Sources.RemoveRange(await db.Sources.Where(s => s.ItemId == id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }
}
