using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Investments.Contracts;

public enum EnvelopeKind { Checking, Savings, Pea, LifeInsurance, Per, Brokerage, Other, Tontine }
public enum ValuationCadence { Monthly, Quarterly, Yearly, Manual }

/// <summary>Issue #399: the regulated livret a savings envelope is, which sets its ceiling and rate; none for any other.</summary>
public enum RegulatedKind { LivretA, Ldds, Lep, LivretJeune }

public static class EnvelopeWire
{
    public static string Of(EnvelopeKind kind) => kind switch
    {
        EnvelopeKind.Checking => "checking", EnvelopeKind.Savings => "savings", EnvelopeKind.Pea => "pea",
        EnvelopeKind.LifeInsurance => "life_insurance", EnvelopeKind.Per => "per", EnvelopeKind.Brokerage => "brokerage",
        EnvelopeKind.Other => "other", EnvelopeKind.Tontine => "tontine", _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string Of(ValuationCadence cadence) => cadence switch
    {
        ValuationCadence.Monthly => "monthly", ValuationCadence.Quarterly => "quarterly",
        ValuationCadence.Yearly => "yearly", ValuationCadence.Manual => "manual",
        _ => throw new ArgumentOutOfRangeException(nameof(cadence)),
    };

    public static string Of(RegulatedKind kind) => kind switch
    {
        RegulatedKind.LivretA => "livret_a", RegulatedKind.Ldds => "ldds", RegulatedKind.Lep => "lep", RegulatedKind.LivretJeune => "livret_jeune",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static bool TryRegulated(string? wire, out RegulatedKind kind)
    {
        foreach (var k in Enum.GetValues<RegulatedKind>())
            if (Of(k) == wire) { kind = k; return true; }
        kind = default;
        return false;
    }

    public static bool TryKind(string? wire, out EnvelopeKind kind)
    {
        foreach (var k in Enum.GetValues<EnvelopeKind>())
            if (Of(k) == wire) { kind = k; return true; }
        kind = default;
        return false;
    }

    public static bool TryCadence(string? wire, out ValuationCadence cadence)
    {
        foreach (var c in Enum.GetValues<ValuationCadence>())
            if (Of(c) == wire) { cadence = c; return true; }
        cadence = default;
        return false;
    }

    /// <summary>Flow kinds on the wire (Tasks 4-6): contribution, withdrawal, opening, income, expense.</summary>
    public static string FlowWire(FlowKind kind) => kind switch
    {
        FlowKind.Contribution => "contribution", FlowKind.Withdrawal => "withdrawal", FlowKind.Opening => "opening",
        FlowKind.Income => "income", FlowKind.Expense => "expense",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static bool TryFlowKind(string? wire, out FlowKind kind)
    {
        foreach (var k in Enum.GetValues<FlowKind>())
            if (FlowWire(k) == wire) { kind = k; return true; }
        kind = default;
        return false;
    }
}

/// <summary>The module's attributes of an envelope (spec §3.2); name, owners, visibility and currency are the item's.</summary>
public sealed record Envelope(
    ItemId ItemId, EnvelopeKind Kind, string? Institution, string? AccountHint, LocalDate? OpenedOn,
    ValuationCadence Cadence, int? StaleAfterDays, string? Note, LocalDate? ClosedOn = null, decimal? UcShareFloorPct = null, decimal? PremiumsBefore2017 = null,
    RegulatedKind? Regulated = null);

public sealed record ImportedEnvelope(ItemId Item, string Hash, PersonId ImportedBy);
public sealed record StoredEnvelope(Envelope Envelope, ImportMark? Import);

/// <summary>A change: a null field is left as it is; <c>Clear*</c> empties an optional one.</summary>
public sealed record EnvelopeChange(
    EnvelopeKind? Kind = null, string? Institution = null, bool ClearInstitution = false,
    string? AccountHint = null, bool ClearAccountHint = false, LocalDate? OpenedOn = null, bool ClearOpenedOn = false,
    ValuationCadence? Cadence = null, int? StaleAfterDays = null, bool ClearStaleAfterDays = false,
    string? Note = null, bool ClearNote = false, decimal? UcShareFloorPct = null, bool ClearUcShareFloorPct = false,
    decimal? PremiumsBefore2017 = null, bool ClearPremiumsBefore2017 = false, RegulatedKind? Regulated = null, bool ClearRegulated = false);

public interface IEnvelopeStore
{
    Task CreateAsync(Envelope envelope, CancellationToken ct);

    Task CreateAsync(Envelope envelope, ImportMark? import, CancellationToken ct);

    /// <summary>2B1b: an import's replacement: every attribute, the closing date and the mark, in place.</summary>
    Task ReplaceAsync(Envelope envelope, ImportMark import, CancellationToken ct);

    Task SetClosedOnAsync(ItemId item, LocalDate? closedOn, CancellationToken ct);

    Task<IReadOnlyDictionary<string, IReadOnlyList<ImportedEnvelope>>> FindImportedAsync(IReadOnlyCollection<string> refs, CancellationToken ct);

    Task<IReadOnlyList<StoredEnvelope>> ReadAllAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct);

    /// <summary>Null when the item is not an envelope (or does not exist).</summary>
    Task<Envelope?> FindAsync(ItemId item, CancellationToken ct);

    Task<IReadOnlyList<Envelope>> FindManyAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct);

    Task UpdateAsync(ItemId item, EnvelopeChange change, CancellationToken ct);
}
