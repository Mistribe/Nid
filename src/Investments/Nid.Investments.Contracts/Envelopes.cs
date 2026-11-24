using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Investments.Contracts;

/// <summary>#182: <see cref="EmployeeSavings"/> is a company savings plan (PEE), <see cref="CollectivePer"/> a collective retirement plan (PERCOL).</summary>
public enum EnvelopeKind { Checking, Savings, Pea, LifeInsurance, Per, Brokerage, Other, Tontine, EmployeeSavings, CollectivePer, Scpi }
public enum ValuationCadence { Monthly, Quarterly, Yearly, Manual }

/// <summary>Issue #399: the regulated livret a savings envelope is, which sets its ceiling and rate; none for any other.</summary>
public enum RegulatedKind { LivretA, Ldds, Lep, LivretJeune }

/// <summary>#182: what several kinds share.</summary>
public static class EnvelopeKinds
{
    /// <summary>A retirement plan, individual or collective: locked until retirement.</summary>
    public static bool IsRetirement(EnvelopeKind kind) => kind is EnvelopeKind.Per or EnvelopeKind.CollectivePer;

    /// <summary>An employee savings plan (PEE, PERCOL): one employee's, so exactly one adult owns it.</summary>
    public static bool IsEmployeePlan(EnvelopeKind kind) => kind is EnvelopeKind.EmployeeSavings or EnvelopeKind.CollectivePer;
}

public static class EnvelopeWire
{
    public static string Of(EnvelopeKind kind) => kind switch
    {
        EnvelopeKind.Checking => "checking", EnvelopeKind.Savings => "savings", EnvelopeKind.Pea => "pea",
        EnvelopeKind.LifeInsurance => "life_insurance", EnvelopeKind.Per => "per", EnvelopeKind.Brokerage => "brokerage",
        EnvelopeKind.Other => "other", EnvelopeKind.Tontine => "tontine",
        EnvelopeKind.EmployeeSavings => "employee_savings", EnvelopeKind.CollectivePer => "collective_per", EnvelopeKind.Scpi => "scpi", _ => throw new ArgumentOutOfRangeException(nameof(kind)),
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
    RegulatedKind? Regulated = null, IReadOnlyList<AvailabilityTranche>? Availability = null, EarlyRelease? EarlyRelease = null);

/// <summary>#184: a part of an employee savings plan released on a date, as its statement prints it on the statement's date.</summary>
public sealed record AvailabilityTranche(LocalDate AvailableOn, decimal Amount);

/// <summary>
/// #466: a legal case that releases an employee savings plan before its dates (<see cref="EarlyReleaseCases"/>), from <c>On</c>:
/// <c>Amount</c> of it, or the whole plan when null. Eligibility is the household's to state, not checked.
/// </summary>
public sealed record EarlyRelease(string Case, LocalDate On, decimal? Amount);

public static class EarlyReleaseCases
{
    public static readonly IReadOnlyList<string> All =
    [
        "home_purchase", "energy_renovation", "marriage", "third_child", "divorce", "domestic_violence", "end_of_contract",
        "business_creation", "disability", "death", "over_indebtedness", "other",
    ];
}

/// <summary>#511: what other modules read of envelopes (Banking: the kind of an account's envelope); never a write.</summary>
public interface IEnvelopes
{
    /// <summary>Null when the item is not an envelope (or does not exist).</summary>
    Task<Envelope?> FindAsync(ItemId item, CancellationToken ct);

    /// <summary>#180: <paramref name="kind"/>, only the envelopes of that kind (the query filters).</summary>
    Task<IReadOnlyList<Envelope>> FindManyAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct, EnvelopeKind? kind = null);
}
