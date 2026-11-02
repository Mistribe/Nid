using Nid.Childcare.Contracts;
using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Childcare.Application;

/// <summary>Spec §1.8–9: one arrangement per item — the child and the carer — whatever its revisions.</summary>
public sealed record Arrangement(ItemId ItemId, PersonId Child, string CarerName, string? CarerPajemploi, string? CarerApproval, uint Version);

/// <summary>Spec §1.8, §2: the figures of one revision, in force from <c>From</c> to <c>To</c> (open while current).</summary>
public sealed record Terms(
    ItemId ItemId, LocalDate From, LocalDate? To, decimal NetHourlyRate, decimal? GrossHourlyRate, decimal OvertimeRate, decimal UpkeepPer9h,
    decimal? MealPrice, int ProgrammedWeeks, IReadOnlyList<WeekdaySlot> Schedule);

public interface IChildcareStore
{
    Task CreateAsync(Arrangement arrangement, Terms terms, CancellationToken ct);

    Task<Arrangement?> FindAsync(ItemId id, CancellationToken ct);

    Task<IReadOnlyList<Arrangement>> FindManyAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct);

    /// <summary>Always writes the row; fails with <see cref="ChildcareCodes.Conflict"/> when it changed since <see cref="Arrangement.Version"/> was read.</summary>
    Task<Result> UpdateAsync(Arrangement arrangement, CancellationToken ct);

    /// <summary>Every revision of these arrangements, by item then <c>From</c>.</summary>
    Task<IReadOnlyList<Terms>> TermsOfAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct);

    /// <summary>Corrects the current revision in place: the row with the same <c>From</c>, its schedule replaced.</summary>
    Task ReplaceCurrentTermsAsync(Terms terms, CancellationToken ct);

    /// <summary>A revision: the current one ends the day before <paramref name="terms"/> starts, in the same save.</summary>
    Task AddTermsAsync(Terms terms, CancellationToken ct);

    Task<IReadOnlyList<(ItemId Item, CareGap Gap)>> GapsOfAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct);

    Task ReplaceGapsAsync(ItemId id, IReadOnlyList<CareGap> gaps, CancellationToken ct);

    /// <summary>Sets or clears the current revision's end.</summary>
    Task SetEndAsync(ItemId id, LocalDate? to, CancellationToken ct);
}
