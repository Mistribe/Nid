using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Investments.Infrastructure.Persistence;

public sealed class EnvelopeStore(InvestmentsDbContext db) : IEnvelopeStore
{
    public Task CreateAsync(Envelope envelope, CancellationToken ct) => CreateAsync(envelope, null, ct);

    public async Task CreateAsync(Envelope envelope, ImportMark? import, CancellationToken ct)
    {
        var row = new EnvelopeRow
        {
            ItemId = envelope.ItemId.Value,
            Kind = EnvelopeWire.Of(envelope.Kind),
            Institution = envelope.Institution,
            AccountHint = envelope.AccountHint,
            OpenedOn = envelope.OpenedOn,
            ValuationCadence = EnvelopeWire.Of(envelope.Cadence),
            StaleAfterDays = envelope.StaleAfterDays,
            Note = envelope.Note,
            ClosedOn = envelope.ClosedOn,
        };
        Mark(row, import);
        db.Envelopes.Add(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task ReplaceAsync(Envelope envelope, ImportMark import, CancellationToken ct)
    {
        var row = await db.Envelopes.SingleAsync(e => e.ItemId == envelope.ItemId.Value, ct);
        row.Kind = EnvelopeWire.Of(envelope.Kind);
        row.Institution = envelope.Institution;
        row.AccountHint = envelope.AccountHint;
        row.OpenedOn = envelope.OpenedOn;
        row.ValuationCadence = EnvelopeWire.Of(envelope.Cadence);
        row.StaleAfterDays = envelope.StaleAfterDays;
        row.Note = envelope.Note;
        row.ClosedOn = envelope.ClosedOn;
        Mark(row, import);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetClosedOnAsync(ItemId item, LocalDate? closedOn, CancellationToken ct)
    {
        var row = await db.Envelopes.SingleAsync(e => e.ItemId == item.Value, ct);
        row.ClosedOn = closedOn;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ImportedEnvelope>>> FindImportedAsync(
        IReadOnlyCollection<string> refs, CancellationToken ct)
    {
        var wanted = refs.Distinct(StringComparer.Ordinal).ToList();
        var rows = await db.Envelopes.AsNoTracking().Where(r => r.ImportRef != null && wanted.Contains(r.ImportRef))
            .Select(r => new { r.ItemId, r.ImportRef, r.ImportHash, r.ImportedBy }).ToListAsync(ct);
        return rows.GroupBy(r => r.ImportRef!, StringComparer.Ordinal).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<ImportedEnvelope>)g.Select(r => new ImportedEnvelope(new ItemId(r.ItemId), r.ImportHash!.Trim(), new PersonId(r.ImportedBy!.Value))).ToList(),
            StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<StoredEnvelope>> ReadAllAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.Value).ToList();
        var rows = await db.Envelopes.AsNoTracking().Where(e => ids.Contains(e.ItemId)).OrderBy(e => e.ItemId).ToListAsync(ct);
        return rows.Select(r => new StoredEnvelope(ToEnvelope(r),
            r.ImportRef is null ? null : new ImportMark(r.ImportRef, r.ImportHash!.Trim(), new PersonId(r.ImportedBy!.Value)))).ToList();
    }

    private static void Mark(EnvelopeRow row, ImportMark? import)
    {
        row.ImportRef = import?.Ref;
        row.ImportHash = import?.Hash;
        row.ImportedBy = import?.ImportedBy.Value;
    }

    public async Task<Envelope?> FindAsync(ItemId item, CancellationToken ct)
        => await db.Envelopes.AsNoTracking().SingleOrDefaultAsync(e => e.ItemId == item.Value, ct) is { } row ? ToEnvelope(row) : null;

    public async Task<IReadOnlyList<Envelope>> FindManyAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.Value).ToList();
        var rows = await db.Envelopes.AsNoTracking().Where(e => ids.Contains(e.ItemId)).ToListAsync(ct);
        return rows.Select(ToEnvelope).ToList();
    }

    public async Task UpdateAsync(ItemId item, EnvelopeChange change, CancellationToken ct)
    {
        var row = await db.Envelopes.SingleAsync(e => e.ItemId == item.Value, ct);
        if (change.Kind is { } kind) row.Kind = EnvelopeWire.Of(kind);
        if (change.ClearInstitution) row.Institution = null; else if (change.Institution is { } institution) row.Institution = institution;
        if (change.ClearAccountHint) row.AccountHint = null; else if (change.AccountHint is { } hint) row.AccountHint = hint;
        if (change.ClearOpenedOn) row.OpenedOn = null; else if (change.OpenedOn is { } opened) row.OpenedOn = opened;
        if (change.Cadence is { } cadence) row.ValuationCadence = EnvelopeWire.Of(cadence);
        if (change.ClearStaleAfterDays) row.StaleAfterDays = null; else if (change.StaleAfterDays is { } days) row.StaleAfterDays = days;
        if (change.ClearNote) row.Note = null; else if (change.Note is { } note) row.Note = note;
        await db.SaveChangesAsync(ct);
    }

    private static Envelope ToEnvelope(EnvelopeRow row)
    {
        if (!EnvelopeWire.TryKind(row.Kind, out var kind) || !EnvelopeWire.TryCadence(row.ValuationCadence, out var cadence))
            throw new InvalidOperationException($"Envelope {row.ItemId} has a stored kind or cadence this code does not know.");
        return new Envelope(new ItemId(row.ItemId), kind, row.Institution, row.AccountHint, row.OpenedOn, cadence, row.StaleAfterDays, row.Note, row.ClosedOn);
    }
}
