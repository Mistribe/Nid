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

/// <summary>
/// Spec §2, decisions 13–14: a month's statement. A draft has its corrected days and no figures; a validated one has every day and its figures, frozen.
/// <c>Days</c> is empty when listed; <c>Version</c> 0 means not stored yet.
/// </summary>
public sealed record Statement(
    ItemId ItemId, YearMonth Month, bool Validated, decimal Mileage, decimal? Cmg, IReadOnlyList<StatementDay> Days, MonthFigures? Figures, uint Version, Instant? ValidatedAt = null);

/// <summary>An arrangement an import created, by its file ref.</summary>
public sealed record ImportedArrangement(ItemId Item, string Hash, PersonId ImportedBy);

public interface IChildcareStore
{
    Task CreateAsync(Arrangement arrangement, Terms terms, CancellationToken ct, ImportMark? import = null);

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

    /// <summary>Every stored month of the arrangement, with its figures, without its days.</summary>
    Task<IReadOnlyList<Statement>> StatementsOfAsync(ItemId id, CancellationToken ct);

    Task<Statement?> FindStatementAsync(ItemId id, YearMonth month, CancellationToken ct);

    /// <summary>
    /// Inserts the draft (version 0) or replaces its days; fails with <see cref="ChildcareCodes.StatementConflict"/> on a stale
    /// version and <see cref="ChildcareCodes.StatementValidated"/> when the stored month is validated.
    /// </summary>
    Task<Result> SaveDraftAsync(Statement draft, CancellationToken ct);

    /// <summary>Freezes the figures and every day of the month (a draft holds its corrections only); the same failures as <see cref="SaveDraftAsync"/>.</summary>
    Task<Result> ValidateAsync(ItemId id, YearMonth month, MonthFigures figures, IReadOnlyList<StatementDay> days, uint version, CancellationToken ct);

    /// <summary>Back to draft: the figures are cleared, the days kept.</summary>
    Task ReopenAsync(ItemId id, YearMonth month, CancellationToken ct);

    /// <summary>Deletes a draft and its days; nothing when there is none.</summary>
    Task DiscardAsync(ItemId id, YearMonth month, CancellationToken ct);

    /// <summary>The arrangements imported under these file refs, by ref (decision 24).</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<ImportedArrangement>>> FindImportedAsync(IReadOnlyCollection<string> refs, CancellationToken ct);

    /// <summary>The import marks of these arrangements, for the export's file ids.</summary>
    Task<IReadOnlyDictionary<ItemId, ImportMark>> ImportMarksAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct);

    /// <summary>An import replacing an arrangement: its child and carer, every revision and its weeks without care, and its mark.</summary>
    Task ReplaceImportedAsync(Arrangement arrangement, IReadOnlyList<Terms> revisions, IReadOnlyList<CareGap> gaps, ImportMark import, CancellationToken ct);

    /// <summary>An imported validated month, its days and frozen figures, inserted or replacing the stored one.</summary>
    Task PutValidatedAsync(Statement statement, CancellationToken ct);
}
