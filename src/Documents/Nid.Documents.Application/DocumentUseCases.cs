using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Nid.Ai.Contracts;
using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.Documents.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Documents.Application;

/// <summary>
/// Design §5: a file and, optionally, its first link. The access behavior checks a <c>core.item</c> target for writing before the
/// handler runs; the handler checks every other rule before the bytes are stored. Syndic documents design §2.1: a
/// <paramref name="SourceRef"/> already stored answers with that document, when the actor reads it. #297: with
/// <paramref name="WarnDuplicate"/>, the same bytes already in a document the actor reads answer with that document, nothing written.
/// </summary>
public sealed record UploadDocumentCommand(
    ReadOnlyMemory<byte> Content, string? FileName, string? Title, LocalDate? DocumentDate, string? Note, DocumentTarget? Link, string? Role,
    string? SourceRef = null, bool FromAssistant = false, bool WarnDuplicate = false)
    : ICommand<UploadedDocument>, IOptionalItemScoped, IRequiresScope
{
    public ItemId? ItemId => Link is { Kind: TargetKinds.CoreItem } link ? new ItemId(link.Id) : null;

    public AccessKind Access => AccessKind.Write;

    /// <summary>
    /// Syndic documents design §2.3: the assistant's `upload_document` (<paramref name="FromAssistant"/>, set by the MCP host only)
    /// files with the proposals' scope, under its own size cap; the REST upload keeps `write:direct` for a token.
    /// </summary>
    public string RequiredScope => FromAssistant ? ScopeNames.Write : ScopeNames.WriteDirect;
}

/// <summary>
/// <paramref name="Existing"/>: the source reference was already stored, nothing was written. <paramref name="Duplicate"/>: the file is
/// already in that document (#297), nothing was written.
/// </summary>
public sealed record UploadedDocument(Guid Id, bool Existing, DocumentSummary? Duplicate = null) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Duplicate?.Items() ?? [];
}

/// <summary>Replaces the title, the date and the note. <paramref name="Version"/>: the one read with the document (#506).</summary>
public sealed record EditDocumentCommand(Guid Id, string Title, LocalDate? DocumentDate, string? Note, uint Version) : ICommand<Guid>, IEditsDocument
{
    Guid IReadsDocument.DocumentId => Id;
}

/// <summary><paramref name="Version"/>: the one read with the document (#506).</summary>
public sealed record SetDocumentArchivedCommand(Guid Id, bool Archived, uint Version) : ICommand<Guid>, IEditsDocument
{
    Guid IReadsDocument.DocumentId => Id;
}

/// <summary>#600: <paramref name="Version"/>: the one read with the document (#506).</summary>
public sealed record DeleteDocumentCommand(Guid Id, uint Version) : ICommand<Guid>, IEditsDocument
{
    Guid IReadsDocument.DocumentId => Id;
}

/// <summary>
/// #305: one document of a filing round: only what is given changes (an empty note removes it). <paramref name="Version"/>: the one read
/// with the document (#506), so a round prepared before someone else's change does not undo it.
/// </summary>
public sealed record DocumentEdit(Guid Id, string? Title, LocalDate? DocumentDate, string? Note, uint Version);

/// <summary>#305: the role of one link in a filing round.</summary>
public sealed record DocumentRoleChange(Guid Id, DocumentTarget Target, string Role);

/// <summary>
/// #305: a filing round, all of it or nothing: each line runs <see cref="EditDocumentCommand"/> or <see cref="SetLinkRoleCommand"/>, with
/// their rights and rules, so one proposal covers dozens of documents and one approval applies them.
/// </summary>
public sealed record FileDocumentsCommand(IReadOnlyList<DocumentEdit> Edits, IReadOnlyList<DocumentRoleChange> Roles) : ICommand<FiledDocuments>, ITransactional
{
    public const int MaxLines = 200;
}

public sealed record FiledDocuments(int Edited, int Roles);

internal static class DocumentFields
{
    // Builder's choice: 255, as a cleaned file name, so the title taken from one always fits.
    public const int MaxTitle = 255;
    public const int MaxNote = 2000;
    public const int MaxSourceRef = 200;

    public static Failure? Check(string? title, LocalDate? date, string? note, LocalDate today)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > MaxTitle)
            return Invalid($"A title is 1 to {MaxTitle} characters.", "title");
        if (note?.Length > MaxNote)
            return Invalid($"A note is at most {MaxNote} characters.", "note");
        if (date > today)
            return Invalid("A document is not dated in the future.", "documentDate");
        return null;
    }

    public static string? Note(string? note) => string.IsNullOrWhiteSpace(note) ? null : note;

    public static string? SourceRef(string? sourceRef) => string.IsNullOrWhiteSpace(sourceRef) ? null : sourceRef.Trim();

    public static Failure? CheckSourceRef(string? sourceRef)
        => sourceRef?.Length > MaxSourceRef ? Invalid($"A source reference is at most {MaxSourceRef} characters.", "sourceRef") : null;

    private static Failure Invalid(string message, string field)
        => new(DocumentsCodes.InvalidField, message, new Dictionary<string, object?> { ["field"] = field });
}

[RequestHandler<UploadDocumentCommand, UploadedDocument>]
public sealed class UploadDocumentHandler(
    IDocumentStore store, IDocumentStorage storage, DocumentAccess access, DocumentsOptions options, IHouseholdCalendar calendar,
    IActorAccessor actor, IUnitOfWork unitOfWork, IClock clock, DocumentTexts texts)
    : IRequestHandler<UploadDocumentCommand, UploadedDocument>
{
    public async ValueTask<Result<UploadedDocument>> HandleAsync(UploadDocumentCommand command, CancellationToken ct = default)
    {
        if (command.Content.Length > options.MaxSizeBytes)
            return Result.Failure<UploadedDocument>(new Failure(DocumentsCodes.TooLarge, $"A document is at most {options.MaxSizeBytes} bytes.", null));

        var fileName = FileNames.Clean(command.FileName);
        var title = string.IsNullOrWhiteSpace(command.Title) ? FileNames.TitleOf(fileName) : command.Title;
        var sourceRef = DocumentFields.SourceRef(command.SourceRef);
        if ((DocumentFields.Check(title, command.DocumentDate, command.Note, await calendar.TodayAsync(ct)) ?? DocumentFields.CheckSourceRef(sourceRef)) is { } invalid)
            return Result.Failure<UploadedDocument>(invalid);
        if (ContentSniffer.MediaTypeOf(command.Content.Span) is not { } mediaType)
            return Result.Failure<UploadedDocument>(new Failure(DocumentsCodes.UnsupportedType, "Only PDF, PNG, JPEG and .eml files are accepted.", null));

        var person = actor.Current!.PersonId!.Value;
        if (sourceRef is not null && await ExistingAsync(sourceRef, person, ct) is { } known)
            return known;

        var id = Guid.CreateVersion7();
        LinkRecord? link = null;
        if (command.Link is { } target)
        {
            // A brand-new document: its uploader edits it, and it has no link yet.
            var checkedLink = await access.CheckLinkAsync(id, person.Value, [], target, command.Role, person, ct);
            if (!checkedLink.TryGetValue(out link))
                return Result.Failure<UploadedDocument>(checkedLink.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        }
        if (command.WarnDuplicate && await DuplicateAsync(command.Content, person, ct) is { } duplicate)
            return Result.Success(new UploadedDocument(duplicate.Id, true, duplicate));

        // Stored before the row: a row never points at missing bytes; bytes left by a failed insert are only unreferenced.
        var sha256 = await storage.PutAsync(command.Content, ct);
        var document = new DocumentRecord(id, sha256, command.Content.Length, mediaType, fileName, title.Trim(), command.DocumentDate,
            DocumentFields.Note(command.Note), person.Value, clock.GetCurrentInstant(), null, sourceRef);
        try
        {
            await using var transaction = await unitOfWork.BeginAsync(ct);
            await store.CreateAsync(document, ct);
            if (link is not null)
                await store.AddLinkAsync(link, ct);
            await transaction.CommitAsync(ct);
        }
        catch (DuplicateSourceRefException)
        {
            // A concurrent upload stored the reference first: answered as if it had come before this one.
            return await ExistingAsync(sourceRef!, person, ct) ?? throw new InvalidOperationException("The source reference vanished.");
        }

        // #167: its text, once stored. An upload never fails for it: the backfill of the next start asks again.
        await texts.TryQueueAsync(document, link is null ? [] : [link], ct);
        return Result.Success(new UploadedDocument(id, false));
    }

    // The latest copy the actor reads; one they do not read is never told apart from none, so the upload goes through as before.
    private async Task<DocumentSummary?> DuplicateAsync(ReadOnlyMemory<byte> content, PersonId person, CancellationToken ct)
    {
        foreach (var found in await store.FindBySha256Async(Convert.ToHexStringLower(SHA256.HashData(content.Span)), ct))
            if ((await access.ReadAsync(found.Id, person, ct)).TryGetValue(out var read))
                return DocumentSummary.Of(read!, await access.LabelsAsync(read!.Links, ct));
        return null;
    }

    // A document the actor does not read is refused without its id: the reference alone says it exists, nothing more.
    private async Task<Result<UploadedDocument>?> ExistingAsync(string sourceRef, PersonId person, CancellationToken ct)
    {
        if (await store.FindBySourceRefAsync(sourceRef, ct) is not { } found)
            return null;
        return (await access.ReadAsync(found.Id, person, ct)).TryGetValue(out _)
            ? Result.Success(new UploadedDocument(found.Id, true))
            : Result.Failure<UploadedDocument>(new ConflictFailure("Another document already has this source reference.") { Code = DocumentsCodes.SourceRefTaken });
    }
}

[RequestHandler<EditDocumentCommand, Guid>]
public sealed class EditDocumentHandler(IDocumentStore store, IHouseholdCalendar calendar, IContextAccessor context)
    : IRequestHandler<EditDocumentCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(EditDocumentCommand command, CancellationToken ct = default)
    {
        var found = CheckedResources.Of<ReadableDocument>(context, command);
        // Checked here too: an edit that leaves the row as it is writes nothing, so the store's token would not see it.
        if (found.Document.Version != command.Version)
            return Result.Failure<Guid>(DocumentConflict.Failure());
        if (DocumentFields.Check(command.Title, command.DocumentDate, command.Note, await calendar.TodayAsync(ct)) is { } invalid)
            return Result.Failure<Guid>(invalid);

        return (await store.EditAsync(command.Id, command.Title.Trim(), command.DocumentDate, DocumentFields.Note(command.Note), command.Version, ct))
            .Map(() => command.Id);
    }
}

[RequestHandler<FileDocumentsCommand, FiledDocuments>]
public sealed class FileDocumentsHandler(IInvoker invoker, IDocumentStore store) : IRequestHandler<FileDocumentsCommand, FiledDocuments>
{
    public async ValueTask<Result<FiledDocuments>> HandleAsync(FileDocumentsCommand command, CancellationToken ct = default)
    {
        var lines = command.Edits.Count + command.Roles.Count;
        if (lines is 0 or > FileDocumentsCommand.MaxLines)
            return Result.Failure<FiledDocuments>(new Failure(DocumentsCodes.InvalidField, $"A filing round has 1 to {FileDocumentsCommand.MaxLines} lines.", null));

        // Every early return is a failure, which rolls back the lines already written.
        foreach (var (edit, i) in command.Edits.Select((e, i) => (e, i)))
        {
            // Unreadable answers as missing: the edit's own access check refuses before anything read here is used.
            var current = await store.FindAsync(edit.Id, ct);
            var edited = await invoker.InvokeAsync(new EditDocumentCommand(edit.Id, edit.Title ?? current?.Title ?? "",
                edit.DocumentDate ?? current?.DocumentDate, edit.Note ?? current?.Note, edit.Version), ct);
            if (edited.TryGetFailure(out var refused))
                return Result.Failure<FiledDocuments>(Located(refused!, $"edits[{i}]"));
        }
        foreach (var (role, i) in command.Roles.Select((r, i) => (r, i)))
        {
            var set = await invoker.InvokeAsync(new SetLinkRoleCommand(role.Id, role.Target, role.Role), ct);
            if (set.TryGetFailure(out var refused))
                return Result.Failure<FiledDocuments>(Located(refused!, $"roles[{i}]"));
        }
        return Result.Success(new FiledDocuments(command.Edits.Count, command.Roles.Count));
    }

    /// <summary>A rule's refusal names its line, so the assistant fixes that one among dozens; not found and forbidden stay what they are.</summary>
    private static Failure Located(Failure failure, string line)
        => FieldErrors.At(failure, failure.Metadata is { } metadata && metadata.TryGetValue("field", out var field) ? $"{line}.{field}" : line);
}

/// <summary>Design §7: an archived document stays readable by the same persons; it leaves the default lists.</summary>
[RequestHandler<SetDocumentArchivedCommand, Guid>]
public sealed class SetDocumentArchivedHandler(IDocumentStore store, IContextAccessor context, IClock clock)
    : IRequestHandler<SetDocumentArchivedCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(SetDocumentArchivedCommand command, CancellationToken ct = default)
    {
        var found = CheckedResources.Of<ReadableDocument>(context, command);
        if (found.Document.Version != command.Version)
            return Result.Failure<Guid>(DocumentConflict.Failure());

        if ((found.Document.ArchivedAt is not null) == command.Archived)
            return Result.Success(command.Id);
        return (await store.SetArchivedAsync(command.Id, command.Archived ? clock.GetCurrentInstant() : null, command.Version, ct)).Map(() => command.Id);
    }
}

/// <summary>
/// #600: whoever edits the document deletes it: the row, its links, text, chunks and embeddings (<see cref="IDocumentStore.DeleteAsync"/>),
/// its text job with its result, then, once committed, its file if no other document has the same bytes. Backups keep it until they expire.
/// </summary>
[RequestHandler<DeleteDocumentCommand, Guid>]
public sealed partial class DeleteDocumentHandler(
    IDocumentStore store, IDocumentStorage storage, IModelJobs jobs, IUnitOfWork unitOfWork, IContextAccessor context, ILogger<DeleteDocumentHandler> logger)
    : IRequestHandler<DeleteDocumentCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(DeleteDocumentCommand command, CancellationToken ct = default)
    {
        var sha256 = CheckedResources.Of<ReadableDocument>(context, command).Document.Sha256;
        await using var transaction = await unitOfWork.BeginAsync(ct);
        if ((await store.DeleteAsync(command.Id, command.Version, ct)).TryGetFailure(out var conflict))
            return Result.Failure<Guid>(conflict!);
        await jobs.ForgetAsync(DocumentTexts.FileRef(command.Id), ct);
        // The bytes go only after the commit, so a rollback never leaves a row without them.
        // ponytail: an upload of the same bytes committed between this check and the delete would lose them; a reference count or a
        // sweep of unreferenced files would close it, if uploads of the same file at the same moment ever become usual.
        if ((await store.FindBySha256Async(sha256, ct)).Count == 0)
            unitOfWork.AfterCommit(() => DeleteFileAsync(sha256));
        await transaction.CommitAsync(ct);
        return Result.Success(command.Id);
    }

    private async Task DeleteFileAsync(string sha256)
    {
        try
        {
            await storage.DeleteAsync(sha256, CancellationToken.None);
        }
        catch (Exception e)
        {
            FileLeft(logger, e, sha256);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The file {Sha256} of a deleted document was not removed; it is no longer referenced")]
    private static partial void FileLeft(ILogger logger, Exception error, string sha256);
}

/// <summary>
/// Design §7: needs the right to edit the document and to write the target; answers the adults the link opens it to.
/// <paramref name="ProposalSource"/>: the link a proposal's source makes (tranche 3 design §4.1), set by the MCP host only; it asks the
/// proposal's own `write` scope instead of `write:direct`, with the same rights.
/// </summary>
public sealed record LinkDocumentCommand(Guid Id, DocumentTarget Target, string Role, bool ProposalSource = false)
    : ICommand<LinkAdded>, IOptionalItemScoped, IRequiresScope, IReadsDocument
{
    Guid IReadsDocument.DocumentId => Id;

    public ItemId? ItemId => Target.Kind == TargetKinds.CoreItem ? new ItemId(Target.Id) : null;

    public AccessKind Access => AccessKind.Write;

    public string RequiredScope => ProposalSource ? ScopeNames.Write : ScopeNames.WriteDirect;
}

/// <summary>The adults who could not read the document before the link and can after; never by which link the others read it.</summary>
public sealed record LinkAdded(IReadOnlyList<Guid> GainedReaders);

/// <summary>Design §7: needs the right to write the target only.</summary>
public sealed record SetLinkRoleCommand(Guid Id, DocumentTarget Target, string Role) : ICommand<Guid>, IOptionalItemScoped
{
    public ItemId? ItemId => Target.Kind == TargetKinds.CoreItem ? new ItemId(Target.Id) : null;

    public AccessKind Access => AccessKind.Write;
}

/// <summary>Design §7: needs the right to write the target only. The uploader keeps the document whatever links remain.</summary>
public sealed record UnlinkDocumentCommand(Guid Id, DocumentTarget Target) : ICommand<Guid>, IOptionalItemScoped
{
    public ItemId? ItemId => Target.Kind == TargetKinds.CoreItem ? new ItemId(Target.Id) : null;

    public AccessKind Access => AccessKind.Write;
}

[RequestHandler<LinkDocumentCommand, LinkAdded>]
public sealed class LinkDocumentHandler(DocumentAccess access, IActorAccessor actor, IContextAccessor context, DocumentTexts texts) : IRequestHandler<LinkDocumentCommand, LinkAdded>
{
    public async ValueTask<Result<LinkAdded>> HandleAsync(LinkDocumentCommand command, CancellationToken ct = default)
    {
        var person = actor.Current!.PersonId!.Value;
        var found = CheckedResources.Of<ReadableDocument>(context, command);

        var d = found.Document;
        var linked = await access.LinkAsync(d.Id, d.UploadedBy, found.Links, command.Target, command.Role, person, ct);
        if (!linked.TryGetValue(out var link))
            return Result.Failure<LinkAdded>(linked.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        await texts.ReclassAsync(d.Id, ct);

        var before = await access.ReadersAsync(d.UploadedBy, found.Links.Select(DocumentAccess.Facts).ToList(), ct);
        var after = await access.ReadersAsync(d.UploadedBy, found.Links.Append(link!).Select(DocumentAccess.Facts).ToList(), ct);
        return Result.Success(new LinkAdded(after.Except(before).Select(p => p.Value).ToList()));
    }
}

[RequestHandler<SetLinkRoleCommand, Guid>]
public sealed class SetLinkRoleHandler(IDocumentStore store, DocumentAccess access, IActorAccessor actor, DocumentTexts texts) : IRequestHandler<SetLinkRoleCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(SetLinkRoleCommand command, CancellationToken ct = default)
    {
        if (!DocumentRoles.All.Contains(command.Role))
            return Result.Failure<Guid>(new Failure(DocumentsCodes.InvalidRole, $"The role '{command.Role}' is not one of {string.Join(", ", DocumentRoles.All)}.",
                new Dictionary<string, object?> { ["field"] = "role" }));

        var link = await access.WritableLinkAsync(command.Id, command.Target, actor.Current!.PersonId!.Value, ct);
        if (!link.TryGetValue(out _))
            return Result.Failure<Guid>(link.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());

        await store.SetRoleAsync(command.Id, command.Target, command.Role, ct);
        await texts.ReclassAsync(command.Id, ct);
        return Result.Success(command.Id);
    }
}

[RequestHandler<UnlinkDocumentCommand, Guid>]
public sealed class UnlinkDocumentHandler(IDocumentStore store, DocumentAccess access, IActorAccessor actor, DocumentTexts texts) : IRequestHandler<UnlinkDocumentCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(UnlinkDocumentCommand command, CancellationToken ct = default)
    {
        var link = await access.WritableLinkAsync(command.Id, command.Target, actor.Current!.PersonId!.Value, ct);
        if (!link.TryGetValue(out _))
            return Result.Failure<Guid>(link.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());

        await store.RemoveLinkAsync(command.Id, command.Target, ct);
        await texts.ReclassAsync(command.Id, ct);
        return Result.Success(command.Id);
    }
}
