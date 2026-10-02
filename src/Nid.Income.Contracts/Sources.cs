using Nid.Core.Contracts;
using NodaTime;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Income.Contracts;

public enum IncomeKind { Salary, SelfEmployment, Pension, Unemployment, FamilyAllowance, SocialBenefit, Alimony, Other }

public static class IncomeWire
{
    public static string Of(IncomeKind kind) => kind switch
    {
        IncomeKind.Salary => "salary", IncomeKind.SelfEmployment => "self_employment", IncomeKind.Pension => "pension",
        IncomeKind.Unemployment => "unemployment", IncomeKind.FamilyAllowance => "family_allowance",
        IncomeKind.SocialBenefit => "social_benefit", IncomeKind.Alimony => "alimony", IncomeKind.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static bool TryKind(string? wire, out IncomeKind kind)
    {
        foreach (var k in Enum.GetValues<IncomeKind>())
            if (Of(k) == wire) { kind = k; return true; }
        kind = default;
        return false;
    }

    public const string OneOffCategory = "one_off";
}

/// <summary>The module's attributes of a source (spec §2); name, owners, visibility, currency and archive are the item's.</summary>
public sealed record IncomeSource(ItemId ItemId, IncomeKind Kind, string? PayerLabel, string? Note, LocalDate? EndedOn = null);

/// <summary>A phase (spec §2): half-open [StartsOn, EndsOn), every month(s) or one year, amounts in the source's currency.</summary>
public sealed record IncomePhase(
    Guid Id, ItemId Source, string Label, LocalDate StartsOn, LocalDate? EndsOn, Money NetAmount, Duration Every,
    Money? GrossAmount, int? MaxCycles, string? Note, uint Version = 0);

/// <summary>A change: a null field is left as it is; <c>Clear*</c> empties an optional one.</summary>
public sealed record IncomeSourceChange(
    IncomeKind? Kind = null, string? PayerLabel = null, bool ClearPayerLabel = false, string? Note = null, bool ClearNote = false);

public interface ISourceStore
{
    Task CreateAsync(IncomeSource source, ImportMark? import, CancellationToken ct);

    /// <summary>Null when the item is not a source (or does not exist).</summary>
    Task<IncomeSource?> FindAsync(ItemId item, CancellationToken ct);

    Task<IReadOnlyList<IncomeSource>> FindManyAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct);

    Task UpdateAsync(ItemId item, IncomeSourceChange change, CancellationToken ct);

    Task SetEndedOnAsync(ItemId item, LocalDate? endedOn, CancellationToken ct);
}

public interface IPhaseStore
{
    /// <summary>By <c>StartsOn</c>, then <c>Label</c>, then <c>Id</c>.</summary>
    Task<IReadOnlyList<IncomePhase>> ForSourceAsync(ItemId source, CancellationToken ct);

    Task<IReadOnlyList<IncomePhase>> ForSourcesAsync(IReadOnlyCollection<ItemId> sources, CancellationToken ct);

    /// <summary>Null when no phase of that id is on that source.</summary>
    Task<IncomePhase?> FindAsync(ItemId source, Guid id, CancellationToken ct);

    Task<IncomePhase> AddAsync(IncomePhase phase, ImportMark? import, CancellationToken ct);

    /// <summary>Throws DbUpdateConcurrencyException when <paramref name="phase"/>.Version is not the stored one.</summary>
    Task<IncomePhase> UpdateAsync(IncomePhase phase, CancellationToken ct);

    Task DeleteAsync(ItemId source, Guid id, uint version, CancellationToken ct);
}
