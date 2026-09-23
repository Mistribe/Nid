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
        "providers", "envelopes", "properties", "property_recurring_costs", "tenancies", "loans", "loan_phases",
        "loan_insurances", "loan_fees", "loan_events", "loan_bank_schedule", "income_sources", "income_phases",
        "valuations", "flows", "documents",
    };
}

[RequestHandler<PutBatchCommand, BatchView>]
public sealed class PutBatchHandler(
    IImportStaging staging, IEnumerable<IImportSection> sections, IActorAccessor accessor, IClock clock, ImportOptions options)
    : IRequestHandler<PutBatchCommand, BatchView>
{
    public async ValueTask<Result<BatchView>> HandleAsync(PutBatchCommand command, CancellationToken ct = default)
    {
        if (!ImportActor.Require(accessor).TryGetValue(out var person))
            return Fail(new UnauthorizedFailure("An import is run by a person.", null));

        var session = await staging.FindOwnedAsync(command.SessionId, person, ct);
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
        if (LongestString(command.Entities) > options.MaxStringLength)
            return Fail(new Failure(ImportCodes.StringTooLong, $"A string holds at most {options.MaxStringLength} characters.", null));

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

        if (await staging.EntityCountAsync(session.Id, ct) + count > options.MaxEntitiesPerSession)
            return Fail(new Failure(ImportCodes.TooManyEntities,
                $"An import holds at most {options.MaxEntitiesPerSession} entities: split it by section.", null));

        var issues = owner.ValidateBatch(command.Section, command.Entities, firstIndex);
        if (issues.Any(i => i.Severity == IssueSeverity.Error))
            return View(command, count, false, issues);

        var now = clock.GetCurrentInstant();
        if (!await staging.AddBatchAsync(session.Id, new StoredBatch(command.Section, command.Index, hash, count, canonical), now, ct))
        {
            // Lost the race for an empty slot. A concurrent PUT of the SAME content is still a replay, not a
            // conflict (§11.2): re-read what actually landed and treat it exactly like the pre-check above.
            var landed = await staging.FindBatchAsync(session.Id, command.Section, command.Index, ct);
            return landed is not null && landed.Hash == hash
                ? View(command, count, true, owner.ValidateBatch(command.Section, command.Entities, firstIndex))
                : Fail(SlotTaken(command));
        }

        // A new batch makes an earlier validation stale (§11.1): the session is open again.
        await staging.TransitionAsync(session.Id, ImportStates.Active.ToList(), ImportState.Open,
            now, now.Plus(NodaTime.Duration.FromTimeSpan(options.SessionLifetime)), null, ct);

        return View(command, count, true, issues);
    }

    private static Result<BatchView> View(PutBatchCommand command, int count, bool accepted, IReadOnlyList<ImportIssue> issues)
        => Result.Success(new BatchView(command.Section, command.Index, count, accepted, issues));

    private static Failure SlotTaken(PutBatchCommand command)
        => new(ImportCodes.SlotTaken, $"Batch {command.Index} of '{command.Section}' was already received with other content.", null);

    private static Result<BatchView> Fail(Failure failure) => Result.Failure<BatchView>(failure);

    private static int LongestString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()!.Length,
        JsonValueKind.Array => element.EnumerateArray().Select(LongestString).DefaultIfEmpty(0).Max(),
        JsonValueKind.Object => element.EnumerateObject()
            .Select(p => Math.Max(p.Name.Length, LongestString(p.Value))).DefaultIfEmpty(0).Max(),
        _ => 0,
    };
}
