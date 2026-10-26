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
/// handler runs; the handler checks every other rule before the bytes are stored.
/// </summary>
public sealed record UploadDocumentCommand(
    ReadOnlyMemory<byte> Content, string? FileName, string? Title, LocalDate? DocumentDate, string? Note, DocumentTarget? Link, string? Role)
    : ICommand<Guid>, IOptionalItemScoped
{
    public ItemId? ItemId => Link is { Kind: TargetKinds.CoreItem } link ? new ItemId(link.Id) : null;

    public AccessKind Access => AccessKind.Write;
}

/// <summary>Replaces the title, the date and the note.</summary>
public sealed record EditDocumentCommand(Guid Id, string Title, LocalDate? DocumentDate, string? Note) : ICommand<Guid>;

public sealed record SetDocumentArchivedCommand(Guid Id, bool Archived) : ICommand<Guid>;

internal static class DocumentFields
{
    // Builder's choice: 255, as a cleaned file name, so the title taken from one always fits.
    public const int MaxTitle = 255;
    public const int MaxNote = 2000;

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

    private static Failure Invalid(string message, string field)
        => new(DocumentsCodes.InvalidField, message, new Dictionary<string, object?> { ["field"] = field });
}

[RequestHandler<UploadDocumentCommand, Guid>]
public sealed class UploadDocumentHandler(
    IDocumentStore store, IDocumentStorage storage, DocumentAccess access, DocumentsOptions options, IHouseholdCalendar calendar,
    IActorAccessor actor, IUnitOfWork unitOfWork, IClock clock)
    : IRequestHandler<UploadDocumentCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(UploadDocumentCommand command, CancellationToken ct = default)
    {
        if (command.Content.Length > options.MaxSizeBytes)
            return Result.Failure<Guid>(new Failure(DocumentsCodes.TooLarge, $"A document is at most {options.MaxSizeBytes} bytes.", null));

        var fileName = FileNames.Clean(command.FileName);
        var title = string.IsNullOrWhiteSpace(command.Title) ? FileNames.TitleOf(fileName) : command.Title;
        if (DocumentFields.Check(title, command.DocumentDate, command.Note, await calendar.TodayAsync(ct)) is { } invalid)
            return Result.Failure<Guid>(invalid);
        if (ContentSniffer.MediaTypeOf(command.Content.Span) is not { } mediaType)
            return Result.Failure<Guid>(new Failure(DocumentsCodes.UnsupportedType, "Only PDF, PNG, JPEG and .eml files are accepted.", null));

        var person = actor.Current!.PersonId!.Value;
        var id = Guid.CreateVersion7();
        LinkRecord? link = null;
        if (command.Link is { } target)
        {
            // A brand-new document: its uploader edits it, and it has no link yet.
            var checkedLink = await access.CheckLinkAsync(id, person.Value, [], target, command.Role, person, ct);
            if (!checkedLink.TryGetValue(out link))
                return Result.Failure<Guid>(checkedLink.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        }

        // Stored before the row: a row never points at missing bytes; bytes left by a failed insert are only unreferenced.
        var sha256 = await storage.PutAsync(command.Content, ct);
        await using var transaction = await unitOfWork.BeginAsync(ct);
        await store.CreateAsync(new DocumentRecord(id, sha256, command.Content.Length, mediaType, fileName, title.Trim(), command.DocumentDate,
            DocumentFields.Note(command.Note), person.Value, clock.GetCurrentInstant(), null), ct);
        if (link is not null)
            await store.AddLinkAsync(link, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(id);
    }
}

[RequestHandler<EditDocumentCommand, Guid>]
public sealed class EditDocumentHandler(IDocumentStore store, DocumentAccess access, IHouseholdCalendar calendar, IActorAccessor actor)
    : IRequestHandler<EditDocumentCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(EditDocumentCommand command, CancellationToken ct = default)
    {
        var editable = await access.EditableAsync(command.Id, actor.Current!.PersonId!.Value, ct);
        if (!editable.TryGetValue(out _))
            return Result.Failure<Guid>(editable.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        if (DocumentFields.Check(command.Title, command.DocumentDate, command.Note, await calendar.TodayAsync(ct)) is { } invalid)
            return Result.Failure<Guid>(invalid);

        await store.EditAsync(command.Id, command.Title.Trim(), command.DocumentDate, DocumentFields.Note(command.Note), ct);
        return Result.Success(command.Id);
    }
}

/// <summary>Design §7: an archived document stays readable by the same persons; it leaves the default lists.</summary>
[RequestHandler<SetDocumentArchivedCommand, Guid>]
public sealed class SetDocumentArchivedHandler(IDocumentStore store, DocumentAccess access, IActorAccessor actor, IClock clock)
    : IRequestHandler<SetDocumentArchivedCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(SetDocumentArchivedCommand command, CancellationToken ct = default)
    {
        var editable = await access.EditableAsync(command.Id, actor.Current!.PersonId!.Value, ct);
        if (!editable.TryGetValue(out var found))
            return Result.Failure<Guid>(editable.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());

        if ((found!.Document.ArchivedAt is not null) != command.Archived)
            await store.SetArchivedAsync(command.Id, command.Archived ? clock.GetCurrentInstant() : null, ct);
        return Result.Success(command.Id);
    }
}

/// <summary>
/// Design §7: needs the right to edit the document and to write the target; answers the adults the link opens it to.
/// <paramref name="ProposalSource"/>: the link a proposal's source makes (tranche 3 design §4.1), set by the MCP host only; it asks the
/// proposal's own `write` scope instead of `write:direct`, with the same rights.
/// </summary>
public sealed record LinkDocumentCommand(Guid Id, DocumentTarget Target, string Role, bool ProposalSource = false)
    : ICommand<LinkAdded>, IOptionalItemScoped, IRequiresScope
{
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
public sealed class LinkDocumentHandler(DocumentAccess access, IActorAccessor actor) : IRequestHandler<LinkDocumentCommand, LinkAdded>
{
    public async ValueTask<Result<LinkAdded>> HandleAsync(LinkDocumentCommand command, CancellationToken ct = default)
    {
        var person = actor.Current!.PersonId!.Value;
        var read = await access.ReadAsync(command.Id, person, ct);
        if (!read.TryGetValue(out var found))
            return Result.Failure<LinkAdded>(read.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());

        var d = found!.Document;
        var linked = await access.LinkAsync(d.Id, d.UploadedBy, found.Links, command.Target, command.Role, person, ct);
        if (!linked.TryGetValue(out var link))
            return Result.Failure<LinkAdded>(linked.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());

        var before = await access.ReadersAsync(d.UploadedBy, found.Links.Select(DocumentAccess.Facts).ToList(), ct);
        var after = await access.ReadersAsync(d.UploadedBy, found.Links.Append(link!).Select(DocumentAccess.Facts).ToList(), ct);
        return Result.Success(new LinkAdded(after.Except(before).Select(p => p.Value).ToList()));
    }
}

[RequestHandler<SetLinkRoleCommand, Guid>]
public sealed class SetLinkRoleHandler(IDocumentStore store, DocumentAccess access, IActorAccessor actor) : IRequestHandler<SetLinkRoleCommand, Guid>
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
        return Result.Success(command.Id);
    }
}

[RequestHandler<UnlinkDocumentCommand, Guid>]
public sealed class UnlinkDocumentHandler(IDocumentStore store, DocumentAccess access, IActorAccessor actor) : IRequestHandler<UnlinkDocumentCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(UnlinkDocumentCommand command, CancellationToken ct = default)
    {
        var link = await access.WritableLinkAsync(command.Id, command.Target, actor.Current!.PersonId!.Value, ct);
        if (!link.TryGetValue(out _))
            return Result.Failure<Guid>(link.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());

        await store.RemoveLinkAsync(command.Id, command.Target, ct);
        return Result.Success(command.Id);
    }
}
