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
            PremiumsBefore2017 = envelope.PremiumsBefore2017,
            Regulated = envelope.Regulated is { } regulated ? EnvelopeWire.Of(regulated) : null,
            Availability = AvailabilityJson.Write(envelope.Availability),
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
        row.PremiumsBefore2017 = envelope.PremiumsBefore2017;
        // The file carries no regulated livret: the one set in Nid stays, unless the envelope is no longer a savings account.
        if (envelope.Kind != EnvelopeKind.Savings) row.Regulated = null;
        // #184: nor any availability date, kept unless the envelope is no longer an employee savings plan.
        if (envelope.Kind != EnvelopeKind.EmployeeSavings) row.Availability = null;
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

    public async Task<IReadOnlyList<Envelope>> FindManyAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct, EnvelopeKind? kind = null)
    {
        var ids = items.Select(i => i.Value).ToList();
        var wire = kind is { } k ? EnvelopeWire.Of(k) : null;
        var rows = await db.Envelopes.AsNoTracking().Where(e => ids.Contains(e.ItemId) && (wire == null || e.Kind == wire)).ToListAsync(ct);
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
        if (change.ClearUcShareFloorPct) row.UcShareFloorPct = null; else if (change.UcShareFloorPct is { } floor) row.UcShareFloorPct = floor;
        if (change.ClearPremiumsBefore2017) row.PremiumsBefore2017 = null; else if (change.PremiumsBefore2017 is { } premiums) row.PremiumsBefore2017 = premiums;
        if (change.ClearRegulated) row.Regulated = null; else if (change.Regulated is { } regulated) row.Regulated = EnvelopeWire.Of(regulated);
        if (change.ClearAvailability) row.Availability = null; else if (change.Availability is { } tranches) row.Availability = AvailabilityJson.Write(tranches);
        await db.SaveChangesAsync(ct);
    }

    private static Envelope ToEnvelope(EnvelopeRow row)
    {
        if (!EnvelopeWire.TryKind(row.Kind, out var kind) || !EnvelopeWire.TryCadence(row.ValuationCadence, out var cadence))
            throw new InvalidOperationException($"Envelope {row.ItemId} has a stored kind or cadence this code does not know.");
        return new Envelope(new ItemId(row.ItemId), kind, row.Institution, row.AccountHint, row.OpenedOn, cadence, row.StaleAfterDays, row.Note, row.ClosedOn, row.UcShareFloorPct, row.PremiumsBefore2017,
            EnvelopeWire.TryRegulated(row.Regulated, out var regulated) ? regulated : (RegulatedKind?)null, AvailabilityJson.Read(row.Availability));
    }
}

/// <summary>#184: the tranches as stored, dates in ISO and amounts as invariant strings so no precision is lost.</summary>
internal static class AvailabilityJson
{
    private sealed record Tranche(string AvailableOn, string Amount);

    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);

    // An empty list is stored as none: the plan is then locked as a whole.
    public static string? Write(IReadOnlyList<AvailabilityTranche>? tranches)
        => tranches is not { Count: > 0 } ? null : System.Text.Json.JsonSerializer.Serialize(
            tranches.OrderBy(t => t.AvailableOn).Select(t => new Tranche(
                NodaTime.Text.LocalDatePattern.Iso.Format(t.AvailableOn), t.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList(), Options);

    public static IReadOnlyList<AvailabilityTranche>? Read(string? json)
        => json is null ? null : System.Text.Json.JsonSerializer.Deserialize<List<Tranche>>(json, Options)!.Select(t => new AvailabilityTranche(
            NodaTime.Text.LocalDatePattern.Iso.Parse(t.AvailableOn).Value, decimal.Parse(t.Amount, System.Globalization.CultureInfo.InvariantCulture))).ToList();
}
