using System.Text.Json;
using Nid.Platform.Canonical;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Import;

public sealed record PutBatchCommand(Guid SessionId, string Section, int Index, string Hash, JsonElement Entities)
    : ICommand<BatchView>, IDirectWrite;

/// <summary><paramref name="Accepted"/> false: the batch had errors and was not staged; its slot is still free.</summary>
public sealed record BatchView(string Section, int Index, int Count, bool Accepted, IReadOnlyList<ImportIssue> Issues);

public static class ImportSections
{
    /// <summary>Sections of <c>import-format.md</c> §3 whose module does not exist yet, or not in this form.</summary>
    public static IReadOnlySet<string> NotYetSupported { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "properties", "property_recurring_costs", "tenancies", "loans", "loan_phases",
        "loan_insurances", "loan_fees", "loan_events", "loan_bank_schedule", "income_sources", "income_phases",
        "documents",
    };
}

[RequestHandler<PutBatchCommand, BatchView>]
public sealed class PutBatchHandler(
    IImportStaging staging, IUnitOfWork unitOfWork, IEnumerable<IImportSection> sections, IActorAccessor accessor, IClock clock,
    ImportOptions options)
    : IRequestHandler<PutBatchCommand, BatchView>
{
    public async ValueTask<Result<BatchView>> HandleAsync(PutBatchCommand command, CancellationToken ct = default)
    {
        if (!ImportActor.Require(accessor).TryGetValue(out var person))
            return Fail(new UnauthorizedFailure("An import is run by a person.", null));

        var session = await ImportActor.FindAsync(staging, clock, command.SessionId, person, ct);
        if (session is null)
            return Fail(ImportActor.NotFound(command.SessionId));
        if (!ImportStates.Active.Contains(session.State))
            return Fail(new Failure(ImportCodes.State, $"This import is {ImportStates.Name(session.State)}: it takes no more batches.", null));

        var owner = sections.FirstOrDefault(s => s.Sections.Contains(command.Section));
        if (owner is null)
            return Fail(ImportSections.NotYetSupported.Contains(command.Section)
                ? new Failure(ImportCodes.SectionNotSupported, $"The section '{command.Section}' is not supported yet.", null)
                : new Failure(ImportCodes.UnknownSection, $"'{command.Section}' is not a section of nid.data.", null));

        if (command.Index < 0 || command.Entities.ValueKind != JsonValueKind.Array)
            return Fail(new Failure(ImportCodes.MalformedEntity, "A batch has an index of 0 or more and an array of entities.", null));

        var count = command.Entities.GetArrayLength();
        if (count > options.MaxEntitiesPerBatch)
            return Fail(new Failure(ImportCodes.TooManyEntities, $"A batch holds at most {options.MaxEntitiesPerBatch} entities.", null));
        var (longest, holdsNul) = Strings(command.Entities);
        if (longest > options.MaxStringLength)
            return Fail(new Failure(ImportCodes.StringTooLong, $"A string holds at most {options.MaxStringLength} characters.", null));
        // RFC 8785 writes U+0000 as \u0000, which PostgreSQL's jsonb refuses: the batch could never be staged.
        if (holdsNul)
            return Fail(new Failure(ImportCodes.MalformedEntity, "A string or a member name holds no U+0000 character.", null));

        string hash, canonical;
        try
        {
            canonical = Rfc8785.Canonicalize(command.Entities);
            hash = Rfc8785.Hash(command.Entities);
        }
        catch (FormatException e)
        {
            return Fail(new Failure(ImportCodes.MalformedEntity, e.Message, null));
        }

        if (!string.Equals(hash, command.Hash, StringComparison.Ordinal))
            return Fail(new Failure(ImportCodes.HashMismatch, "The hash is not the RFC 8785 SHA-256 of these entities.", null));

        var firstIndex = command.Index * options.MaxEntitiesPerBatch;
        var existing = await staging.FindBatchAsync(session.Id, command.Section, command.Index, ct);
        if (existing is not null)
            return existing.Hash == hash
                ? View(command, count, true, owner.ValidateBatch(command.Section, command.Entities, firstIndex)) // replay (§11.2)
                : Fail(SlotTaken(command));

        var issues = owner.ValidateBatch(command.Section, command.Entities, firstIndex);
        if (issues.Any(i => i.Severity == IssueSeverity.Error))
            return View(command, count, false, issues);

        var now = clock.GetCurrentInstant();

        // A new batch makes an earlier validation stale (§11.1): the session is open again. The move and the
        // insert are one transaction, and the move row-locks the session until that transaction ends. So:
        // - a commit that moved the session first makes this move wait, then find it no longer active: the
        //   batch is refused before anything is written, never staged behind a commit that already cleared;
        // - a validate or commit arriving after this move waits for the batch to be committed, then sees it
        //   (validate refuses its now stale report; commit finds the session open and refuses).
        bool added;
        await using (var transaction = await unitOfWork.BeginAsync(ct))
        {
            if (!await staging.TransitionAsync(session.Id, ImportStates.Active.ToList(), ImportState.Open,
                    now, now.Plus(NodaTime.Duration.FromTimeSpan(options.SessionLifetime)), null, ct))
                return Fail(new Failure(ImportCodes.State, "This import is no longer taking batches.", null));

            // Counted under the lock: two batches racing into one session cannot both squeeze under the bound.
            if (await staging.EntityCountAsync(session.Id, ct) + count > options.MaxEntitiesPerSession)
                return Fail(new Failure(ImportCodes.TooManyEntities,
                    $"An import holds at most {options.MaxEntitiesPerSession} entities: split it by section.", null));

            added = await staging.AddBatchAsync(session.Id, new StoredBatch(command.Section, command.Index, hash, count, canonical), now, ct);
            if (added)
                await transaction.CommitAsync(ct);
        } // not committed: the move is rolled back with the (aborted) insert

        if (!added)
        {
            // Lost the race for an empty slot: the unique violation aborted the transaction, which is rolled
            // back by now. A concurrent PUT of the SAME content is still a replay, not a conflict (§11.2):
            // re-read what actually landed, outside the transaction, and treat it like the pre-check above.
            var landed = await staging.FindBatchAsync(session.Id, command.Section, command.Index, ct);
            return landed is not null && landed.Hash == hash
                ? View(command, count, true, owner.ValidateBatch(command.Section, command.Entities, firstIndex))
                : Fail(SlotTaken(command));
        }

        return View(command, count, true, issues);
    }

    private static Result<BatchView> View(PutBatchCommand command, int count, bool accepted, IReadOnlyList<ImportIssue> issues)
        => Result.Success(new BatchView(command.Section, command.Index, count, accepted, issues));

    private static Failure SlotTaken(PutBatchCommand command)
        => new(ImportCodes.SlotTaken, $"Batch {command.Index} of '{command.Section}' was already received with other content.", null);

    private static Result<BatchView> Fail(Failure failure) => Result.Failure<BatchView>(failure);

    /// <summary>The longest string or member name, and whether any of them holds U+0000.</summary>
    private static (int Longest, bool HoldsNul) Strings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString()!;
                return (value.Length, value.Contains('\0'));
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(Strings).Aggregate((0, false), Combine);
            case JsonValueKind.Object:
                return element.EnumerateObject()
                    .Select(p => Combine((p.Name.Length, p.Name.Contains('\0')), Strings(p.Value)))
                    .Aggregate((0, false), Combine);
            default:
                return (0, false);
        }

        static (int, bool) Combine((int Longest, bool HoldsNul) a, (int Longest, bool HoldsNul) b)
            => (Math.Max(a.Longest, b.Longest), a.HoldsNul || b.HoldsNul);
    }
}
