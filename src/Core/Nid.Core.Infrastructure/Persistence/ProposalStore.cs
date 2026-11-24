using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Application;
using Nid.Core.Contracts;
using UnambitiousFx.Engram;
using Npgsql;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Infrastructure.Persistence;

/// <summary>core-data-model.md §7 on <c>core.proposal</c> (spec 4C1 §2–§4).</summary>
public sealed partial class ProposalStore(CoreDbContext db, IClock clock, IEnumerable<IAppliedDocumentStamp> moduleStamps) : IProposalStore
{
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(IReadOnlyList<ProposalWarning>))]
    [JsonSerializable(typeof(List<ProposalWarning>))]
    [JsonSerializable(typeof(JsonObject))]
    private sealed partial class ProposalJson : JsonSerializerContext;

    /// <summary>The document enters only when set: a proposal without one keeps the fingerprint it had before documents.</summary>
    /// <summary>#498: an assistant proposal's carries its author, so it merges only with that author's own (decision N).</summary>
    public static string Fingerprint(string kind, ItemId? target, JsonElement payload, Guid? document, Guid? assistantFor = null)
    {
        var fields = new JsonObject { ["kind"] = kind, ["target"] = target?.Value.ToString(), ["payload"] = JsonNode.Parse(payload.GetRawText()) };
        if (document is { } id)
            fields["document"] = id.ToString();
        if (assistantFor is { } author)
            fields["assistant_for"] = author.ToString();
        return JsonCanonicalizer.Hash(JsonSerializer.SerializeToElement(fields, ProposalJson.Default.JsonObject));
    }

    public async Task<SubmitOutcome> SubmitAsync(NewProposal p, CancellationToken ct)
    {
        var assistant = p.CreatedVia == AssistantProposals.Via;
        var fingerprint = Fingerprint(p.Kind, p.Target, p.Payload, p.DocumentId, assistant ? p.Person.Value : null);
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        // One submission at a time per target (per submitter without one): deduplication and supersession read then write.
        if (p.Target is { } target)
            await db.Database.ExecuteSqlAsync($"select 1 from core.item where id = {target.Value} for update", ct);
        else
            await db.Database.ExecuteSqlAsync($"select 1 from core.person where id = {p.Person.Value} for update", ct);

        var same = await db.Proposals.AsNoTracking().SingleOrDefaultAsync(r => r.Fingerprint == fingerprint && r.Status == "pending", ct);
        if (same is not null)
            return new SubmitOutcome(ToStored(same), false);

        var row = new ProposalRow
        {
            Id = Guid.CreateVersion7(), Module = p.Module, Kind = p.Kind, TargetItemId = p.Target?.Value, ForPersonId = p.Person.Value,
            SubjectKey = p.SubjectKey, Payload = p.Payload.GetRawText(), PayloadVersion = p.PayloadVersion,
            Warnings = JsonSerializer.Serialize(p.Warnings, ProposalJson.Default.IReadOnlyListProposalWarning), Status = "pending", Fingerprint = fingerprint,
            CreatedVia = p.CreatedVia, TokenId = p.TokenId, CreatedByPersonId = p.Person.Value, SourceLabel = p.SourceLabel,
            CreatedAt = clock.GetCurrentInstant(), DocumentId = p.DocumentId,
        };
        var superseded = new List<Guid>();
        if (p.SubjectKey is not null)
            // Decision N: an assistant proposal replaces only its author's pending assistant ones, and nothing else replaces those.
            foreach (var older in await db.Proposals.Where(r => r.Kind == p.Kind && r.TargetItemId == row.TargetItemId
                         && r.SubjectKey == p.SubjectKey && r.Status == "pending"
                         && (assistant
                             ? r.CreatedVia == AssistantProposals.Via && r.CreatedByPersonId == p.Person.Value
                             : r.CreatedVia != AssistantProposals.Via)).ToListAsync(ct))
            {
                older.Status = "superseded";
                older.SupersededById = row.Id;
                superseded.Add(older.Id);
            }

        db.Proposals.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent identical submission won the unique partial index: answer with its row.
            db.ChangeTracker.Clear();
            var winner = await db.Proposals.AsNoTracking().SingleAsync(r => r.Fingerprint == fingerprint && r.Status == "pending", ct);
            return new SubmitOutcome(ToStored(winner), false);
        }
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new SubmitOutcome(ToStored(row), true, superseded);
    }

    public async Task<StoredProposal?> FindAsync(Guid id, CancellationToken ct)
        => await db.Proposals.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct) is { } row ? ToStored(row) : null;

    public async Task<IReadOnlyList<StoredProposal>> ListAsync(
        IReadOnlySet<ItemId> targets, PersonId? person, ProposalStatus status, CancellationToken ct, PersonId? viewer = null)
    {
        var ids = targets.Select(t => (Guid?)t.Value).ToArray();
        var wire = Wire(status);
        var untargeted = person?.Value;
        var reader = viewer?.Value;
        return (await db.Proposals.AsNoTracking()
                .Where(r => (ids.Contains(r.TargetItemId) || (untargeted != null && r.TargetItemId == null && r.ForPersonId == untargeted)) && r.Status == wire)
                .Where(r => reader == null || r.Status != "pending" || r.CreatedVia != AssistantProposals.Via || r.CreatedByPersonId == reader)
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).ToListAsync(ct)).Select(ToStored).ToList();
    }

    public async Task<Result<StoredProposal>> DecideAsync(Guid id, uint version, ProposalStatus to, PersonId by,
        JsonElement? editedPayload, string? note, IReadOnlyList<Guid> appliedRefs, CancellationToken ct)
    {
        var row = await db.Proposals.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (row is null)
            return Result.Failure<StoredProposal>(new NotFoundFailure("proposal", id.ToString()));
        if (row.Status != "pending")
            return Result.Failure<StoredProposal>(new ConflictFailure("This proposal was already decided or replaced.") { Code = ProposalCodes.Decided });
        if (row.Version != version)
            return Result.Failure<StoredProposal>(new ConflictFailure("This proposal changed meanwhile: reload it.") { Code = ProposalCodes.Conflict });

        row.Status = Wire(to);
        row.DecidedByPersonId = by.Value;
        row.DecidedAt = clock.GetCurrentInstant();
        row.DecisionNote = note;
        row.EditedPayload = editedPayload?.GetRawText();
        row.AppliedRefs = [.. appliedRefs];
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result.Failure<StoredProposal>(new ConflictFailure("This proposal changed meanwhile: reload it.") { Code = ProposalCodes.Conflict });
        }
        return Result.Success(ToStored(row));
    }

    public async Task<Result<Guid>> DeleteAsync(Guid id, uint version, CancellationToken ct)
    {
        var row = await db.Proposals.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (row is null)
            return Result.Failure<Guid>(new NotFoundFailure("proposal", id.ToString()));
        if (row.Status != "pending")
            return Result.Failure<Guid>(new ConflictFailure("This proposal was already decided or replaced.") { Code = ProposalCodes.Decided });
        if (row.Version != version)
            return Result.Failure<Guid>(new ConflictFailure("This proposal changed meanwhile: reload it.") { Code = ProposalCodes.Conflict });

        db.Proposals.Remove(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result.Failure<Guid>(new ConflictFailure("This proposal changed meanwhile: reload it.") { Code = ProposalCodes.Conflict });
        }
        return Result.Success(id);
    }

    public Task AddWarningAsync(Guid id, ProposalWarning warning, CancellationToken ct)
    {
        var item = JsonSerializer.Serialize([warning], ProposalJson.Default.ListProposalWarning);
        return db.Database.ExecuteSqlAsync($"update core.proposal set warnings = warnings || {item}::jsonb where id = {id} and status = 'pending'", ct);
    }

    public async Task StampAsync(Guid documentId, IReadOnlyList<Guid> appliedRefs, CancellationToken ct)
    {
        if (appliedRefs.Count == 0)
            return; // a « conforme » approval wrote nothing
        // Through the change tracker, not raw SQL: the audit interceptor then records the stamp under each fact's item.
        var ids = appliedRefs.ToList();
        foreach (var row in await db.Valuations.Where(r => ids.Contains(r.Id)).ToListAsync(ct))
            row.DocumentId = documentId;
        foreach (var row in await db.Flows.Where(r => ids.Contains(r.Id)).ToListAsync(ct))
            row.DocumentId = documentId;
        await db.SaveChangesAsync(ct);
        foreach (var stamp in moduleStamps)
            await stamp.StampAsync(documentId, appliedRefs, ct);
    }

    private static string Wire(ProposalStatus status) => status.ToString().ToLowerInvariant();

    private static StoredProposal ToStored(ProposalRow r) => new(
        r.Id, r.Module, r.Kind, r.TargetItemId is { } target ? new ItemId(target) : null, new PersonId(r.ForPersonId), r.SubjectKey,
        JsonDocument.Parse(r.Payload).RootElement.Clone(), r.PayloadVersion,
        r.EditedPayload is null ? null : JsonDocument.Parse(r.EditedPayload).RootElement.Clone(),
        JsonSerializer.Deserialize(r.Warnings, ProposalJson.Default.ListProposalWarning) ?? [],
        Enum.Parse<ProposalStatus>(r.Status, ignoreCase: true), r.SupersededById, r.CreatedVia, r.TokenId,
        new PersonId(r.CreatedByPersonId), r.SourceLabel, r.CreatedAt,
        r.DecidedByPersonId is { } d ? new PersonId(d) : null, r.DecidedAt, r.DecisionNote, r.AppliedRefs, r.Version, r.DocumentId);
}
