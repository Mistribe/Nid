using System.Text.Json;
using Nid.Platform.Canonical;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Platform.Import;

/// <summary>
/// Orders the sections, assembles one report, and owns the one transaction of a commit
/// (<c>import-format.md</c> §11.4). It knows sections by name only.
/// </summary>
public sealed class ImportRunner(
    IEnumerable<IImportSection> sections, IImportStaging staging, IUnitOfWork unitOfWork, IClock clock, ImportOptions options)
{
    public const string Validated = "validated";
    public const string Invalid = "invalid";
    public const string Committed = "committed";

    private readonly IReadOnlyList<IImportSection> ordered = sections.OrderBy(s => s.Order).ThenBy(s => s.Module, StringComparer.Ordinal).ToList();

    public async Task<ImportReport> ValidateAsync(ImportSession session, CancellationToken ct)
        => (await RunValidationAsync(session, ct)).Report;

    public async Task<Result<ImportReport>> CommitAsync(ImportSession session, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        ImportReport refused;
        ImportReport? computed = null; // the last report RunValidationAsync produced, so a later exception can still report against it

        try
        {
            // Disposing `transaction` rolls back everything written on it — whether this block runs to the
            // end without committing, or an exception unwinds through it. Either way, disposal happens before
            // the catch below runs: nothing here can leak past a rollback.
            await using var transaction = await unitOfWork.BeginAsync(ct);

            // Moving the session first row-locks it: a concurrent commit waits here, then finds it no longer
            // validated. If this transaction rolls back, so does the move.
            if (!await staging.TransitionAsync(session.Id, [ImportState.Validated], ImportState.Committed, now, session.ExpiresAt, null, ct))
                return Result.Failure<ImportReport>(new Failure(ImportCodes.State, "Only a validated import can be committed.", null));

            // The state may have changed since validate: everything is checked again, on this transaction.
            var (report, context, staged) = await RunValidationAsync(session, ct);
            computed = report;
            if (report.State != Validated)
                refused = report;
            else
            {
                var applied = await ApplyAsync(context, staged, ct);
                if (applied.TryGetValue(out var counts))
                {
                    var final = report with { State = Committed, Sections = counts };
                    await staging.ClearBatchesAsync(session.Id, ct);
                    await staging.TransitionAsync(session.Id, [ImportState.Committed], ImportState.Committed,
                        now, session.ExpiresAt, ImportReportJson.Serialize(final), ct);
                    await transaction.CommitAsync(ct);
                    return Result.Success(final);
                }

                applied.TryGetFailure(out var failure);
                refused = report with
                {
                    Issues = [.. report.Issues, ImportIssue.Error(ImportCodes.ApplyFailed, $"{failure!.Message} ({failure.Code})", "")],
                };
            }
        }
        catch (Exception e)
        {
            // Spec §5: any failure — thrown, not just a Result carrying a failure — rolls everything back and
            // the session stays validated with the error in its report. The transaction is already gone by the
            // time we get here, so this write is its own operation, deliberately off the caller's (possibly
            // already-cancelled or timed-out) token: a cancelled or too-slow commit must still record why, not
            // vanish and leave the session's last report stale.
            var basis = computed ?? new ImportReport(Validated, new SortedDictionary<string, SectionCounts>(StringComparer.Ordinal), []);
            var issue = e is OperationCanceledException
                ? ImportIssue.Error(ImportCodes.TooLarge, "The import did not finish in time and was abandoned: split it into smaller imports.", "")
                : ImportIssue.Error(ImportCodes.ApplyFailed, $"{e.GetType().Name}: {e.Message}", "");
            refused = basis with { Issues = [.. basis.Issues, issue] };

            await staging.TransitionAsync(session.Id, [ImportState.Validated], ImportState.Validated,
                now, session.ExpiresAt, ImportReportJson.Serialize(refused), CancellationToken.None);

            if (e is OperationCanceledException)
                throw; // Bounded.RunAsync turns this into import.too_large (or lets a real cancellation propagate)

            return Result.Failure<ImportReport>(new Failure(ImportCodes.NotCommitted, "Nothing was written: the session's report says why.", null));
        } // not committed: everything above is rolled back

        var state = refused.State == Invalid ? ImportState.Invalid : ImportState.Validated;
        await staging.TransitionAsync(session.Id, [ImportState.Validated], state, now, session.ExpiresAt, ImportReportJson.Serialize(refused), ct);
        return Result.Failure<ImportReport>(new Failure(ImportCodes.NotCommitted, "Nothing was written: the session's report says why.", null));
    }

    private async Task<(ImportReport Report, ImportContext Context, IStagedBatches Staged)> RunValidationAsync(ImportSession session, CancellationToken ct)
    {
        var batches = await staging.BatchesAsync(session.Id, ct);
        var context = new ImportContext(session.Person, session.Adults, clock.GetCurrentInstant().InUtc().Date);
        var staged = Staged.From(batches, options.MaxEntitiesPerBatch);

        var issues = CheckLayout(batches).ToList();
        var counts = new SortedDictionary<string, SectionCounts>(StringComparer.Ordinal);

        // A broken layout makes every global index wrong; the sections would report misleading paths.
        if (issues.Count == 0)
            foreach (var section in ordered)
            {
                var outcome = await section.ValidateAllAsync(context, staged, ct);
                issues.AddRange(outcome.Issues);
                foreach (var (name, sectionCounts) in outcome.Counts)
                    counts[name] = sectionCounts;
            }

        var state = issues.Any(i => i.Severity == IssueSeverity.Error) ? Invalid : Validated;
        return (new ImportReport(state, counts, issues), context, staged);
    }

    private async Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        var counts = new SortedDictionary<string, SectionCounts>(StringComparer.Ordinal);
        foreach (var section in ordered)
        {
            var applied = await section.ApplyAsync(context, staged, ct);
            if (!applied.TryGetValue(out var sectionCounts))
                return applied;
            foreach (var (name, count) in sectionCounts)
                counts[name] = count;
        }

        return Result.Success<IReadOnlyDictionary<string, SectionCounts>>(counts);
    }

    /// <summary>
    /// Global indexes are <c>n × batch size + position</c> (§11.2), which holds only if batches are numbered
    /// from 0 without gaps and every batch but the last of a section is full.
    /// </summary>
    private IEnumerable<ImportIssue> CheckLayout(IReadOnlyList<StoredBatch> batches)
    {
        foreach (var section in batches.GroupBy(b => b.Section, StringComparer.Ordinal))
        {
            var numbered = section.OrderBy(b => b.Index).ToList();
            for (var i = 0; i < numbered.Count; i++)
            {
                if (numbered[i].Index != i)
                {
                    yield return ImportIssue.Error(ImportCodes.BatchLayout,
                        $"Batch {i} of '{section.Key}' is missing: batches are numbered from 0 without gaps.", section.Key);
                    break;
                }

                if (i < numbered.Count - 1 && numbered[i].Count != options.MaxEntitiesPerBatch)
                {
                    yield return ImportIssue.Error(ImportCodes.BatchLayout,
                        $"Batch {i} of '{section.Key}' holds {numbered[i].Count} entities: every batch but the last holds {options.MaxEntitiesPerBatch}.",
                        section.Key);
                    break;
                }
            }
        }
    }

    private sealed class Staged(Dictionary<string, List<StagedEntity>> bySection) : IStagedBatches
    {
        public IReadOnlyList<StagedEntity> Section(string section)
            => bySection.TryGetValue(section, out var entities) ? entities : [];

        public static Staged From(IReadOnlyList<StoredBatch> batches, int batchSize)
        {
            var bySection = new Dictionary<string, List<StagedEntity>>(StringComparer.Ordinal);
            foreach (var batch in batches.OrderBy(b => b.Section, StringComparer.Ordinal).ThenBy(b => b.Index))
            {
                if (!bySection.TryGetValue(batch.Section, out var entities))
                    bySection[batch.Section] = entities = [];

                using var document = JsonDocument.Parse(batch.Payload);
                var position = 0;
                foreach (var entity in document.RootElement.EnumerateArray())
                {
                    var value = entity.Clone();
                    // jsonb gives the payload back in its own layout; the canonical form, hence the hash, is unchanged.
                    entities.Add(new StagedEntity(batch.Index * batchSize + position++, value, Rfc8785.Hash(value)));
                }
            }

            return new Staged(bySection);
        }
    }
}
