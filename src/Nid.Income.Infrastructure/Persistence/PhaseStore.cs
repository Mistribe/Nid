using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Income.Infrastructure.Persistence;

public sealed class PhaseStore(IncomeDbContext db) : IPhaseStore
{
    public Task<IReadOnlyList<IncomePhase>> ForSourceAsync(ItemId source, CancellationToken ct) => ForSourcesAsync([source], ct);

    public async Task<IReadOnlyList<IncomePhase>> ForSourcesAsync(IReadOnlyCollection<ItemId> sources, CancellationToken ct)
    {
        var ids = sources.Select(s => s.Value).ToList();
        var rows = await db.Phases.AsNoTracking().Where(p => ids.Contains(p.SourceItemId)).ToListAsync(ct);
        // Ordered here, not by the database's collation: the same order on every server.
        return rows.Select(ToPhase)
            .OrderBy(p => p.StartsOn).ThenBy(p => p.Label, StringComparer.Ordinal).ThenBy(p => p.Id)
            .ToList();
    }

    public async Task<IncomePhase?> FindAsync(ItemId source, Guid id, CancellationToken ct)
        => await db.Phases.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.SourceItemId == source.Value, ct) is { } row
            ? ToPhase(row)
            : null;

    public async Task<IncomePhase> AddAsync(IncomePhase phase, ImportMark? import, CancellationToken ct)
    {
        var row = new PhaseRow { Id = phase.Id, SourceItemId = phase.Source.Value };
        Fill(row, phase);
        row.ImportRef = import?.Ref;
        row.ImportHash = import?.Hash;
        row.ImportedBy = import?.ImportedBy.Value;
        db.Phases.Add(row);
        await db.SaveChangesAsync(ct);
        return ToPhase(row);
    }

    public async Task<IncomePhase> UpdateAsync(IncomePhase phase, CancellationToken ct)
    {
        var row = await TrackedAsync(phase.Source, phase.Id, phase.Version, ct);
        Fill(row, phase);
        await db.SaveChangesAsync(ct);
        return ToPhase(row);
    }

    public async Task DeleteAsync(ItemId source, Guid id, uint version, CancellationToken ct)
    {
        db.Phases.Remove(await TrackedAsync(source, id, version, ct));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The row, its concurrency token set to the version the caller read: the save then fails with
    /// <see cref="DbUpdateConcurrencyException"/> when someone changed it since. A phase removed since is the same conflict.
    /// </summary>
    private async Task<PhaseRow> TrackedAsync(ItemId source, Guid id, uint version, CancellationToken ct)
    {
        var row = await db.Phases.SingleOrDefaultAsync(p => p.Id == id && p.SourceItemId == source.Value, ct)
                  ?? throw new DbUpdateConcurrencyException($"Phase {id} is no longer on source {source}.");
        db.Entry(row).Property(r => r.Version).OriginalValue = version;
        return row;
    }

    private static void Fill(PhaseRow row, IncomePhase phase)
    {
        row.Label = phase.Label;
        row.StartsOn = phase.StartsOn;
        row.EndsOn = phase.EndsOn;
        row.NetAmount = phase.NetAmount.Amount;
        row.GrossAmount = phase.GrossAmount?.Amount;
        row.Currency = phase.NetAmount.Currency.Code;
        row.EveryCount = phase.Every.Count;
        row.EveryUnit = phase.Every.Unit switch
        {
            DurationUnit.Month => "month",
            DurationUnit.Year => "year",
            _ => throw new ArgumentOutOfRangeException(nameof(phase), "A phase repeats every month(s) or year(s)."),
        };
        row.MaxCycles = phase.MaxCycles;
        row.Note = phase.Note;
    }

    private static IncomePhase ToPhase(PhaseRow row)
    {
        var unit = row.EveryUnit switch
        {
            "month" => DurationUnit.Month,
            "year" => DurationUnit.Year,
            _ => throw new InvalidOperationException($"Phase {row.Id} has a stored cadence this code does not know."),
        };
        if (!Currency.TryCreate(row.Currency.Trim(), out var currency)
            || !Money.Create(row.NetAmount, currency).TryGetValue(out var net)
            || !Duration.Create(row.EveryCount, unit).TryGetValue(out var every))
            throw new InvalidOperationException($"Phase {row.Id} has a stored amount or cadence this code does not read.");
        Money? gross = null;
        if (row.GrossAmount is { } g)
            gross = Money.Create(g, currency).TryGetValue(out var m) ? m : throw new InvalidOperationException($"Phase {row.Id} has an unreadable gross amount.");
        return new IncomePhase(row.Id, new ItemId(row.SourceItemId), row.Label, row.StartsOn, row.EndsOn, net, every,
            gross, row.MaxCycles, row.Note, row.Version);
    }
}
