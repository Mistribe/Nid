using System.Security.Cryptography;
using System.Text;
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
/// #305: everything an upload URL files. The URL holds an opaque token only; its sha256 is the row's key, so the stored rows open nothing.
/// </summary>
public sealed record UploadTicket(
    string TokenHash, Guid PersonId, Guid TokenId, string FileName, string Title, LocalDate DocumentDate, string? Note,
    string TargetKind, Guid TargetId, string Role, string? SourceRef, Instant ExpiresAt);

/// <summary>
/// #305: checks an upload as <see cref="UploadDocumentCommand"/> does, all but the bytes, and answers the token of a URL that files it. The
/// assistant's tool only (the actor's token is the ticket's); the rights are checked again when the file arrives.
/// </summary>
public sealed record RequestDocumentUploadCommand(
    string? FileName, string? Title, LocalDate DocumentDate, string? Note, DocumentTarget Target, string? Role, string? SourceRef)
    : ICommand<DocumentUploadGrant>, IOptionalItemScoped, IRequiresScope
{
    public ItemId? ItemId => Target.Kind == TargetKinds.CoreItem ? new ItemId(Target.Id) : null;

    public AccessKind Access => AccessKind.Write;

    public string RequiredScope => ScopeNames.Write;
}

/// <summary><paramref name="Token"/> exists here and in the URL, nowhere else.</summary>
public sealed record DocumentUploadGrant(string Token, Instant ExpiresAt);

[RequestHandler<RequestDocumentUploadCommand, DocumentUploadGrant>]
public sealed class RequestDocumentUploadHandler(IDocumentStore store, DocumentAccess access, IHouseholdCalendar calendar, IActorAccessor accessor, IClock clock)
    : IRequestHandler<RequestDocumentUploadCommand, DocumentUploadGrant>
{
    /// <summary>Long enough for a client to run its next command; a slow transfer is not cut, the URL is claimed when the request arrives.</summary>
    public static readonly NodaTime.Duration Lifetime = NodaTime.Duration.FromMinutes(10);

    public async ValueTask<Result<DocumentUploadGrant>> HandleAsync(RequestDocumentUploadCommand command, CancellationToken ct = default)
    {
        if (accessor.Current is not { PersonId: { } person, TokenId: { } tokenId })
            return Result.Failure<DocumentUploadGrant>(new Failure(DocumentsCodes.InvalidField, "An upload URL is for a token.", null));

        var fileName = FileNames.Clean(command.FileName);
        var title = string.IsNullOrWhiteSpace(command.Title) ? FileNames.TitleOf(fileName) : command.Title;
        var sourceRef = DocumentFields.SourceRef(command.SourceRef);
        if ((DocumentFields.Check(title, command.DocumentDate, command.Note, await calendar.TodayAsync(ct)) ?? DocumentFields.CheckSourceRef(sourceRef)) is { } invalid)
            return Result.Failure<DocumentUploadGrant>(invalid);
        var link = await access.CheckLinkAsync(Guid.Empty, person.Value, [], command.Target, command.Role, person, ct);
        if (link.TryGetFailure(out var refused))
            return Result.Failure<DocumentUploadGrant>(refused!);

        var token = RandomNumberGenerator.GetHexString(64, lowercase: true);
        var expiresAt = clock.GetCurrentInstant() + Lifetime;
        await store.AddUploadTicketAsync(new UploadTicket(UploadTokens.Hash(token), person.Value, tokenId, fileName, title.Trim(), command.DocumentDate,
            DocumentFields.Note(command.Note), command.Target.Kind, command.Target.Id, command.Role!, sourceRef, expiresAt), clock.GetCurrentInstant(), ct);
        return Result.Success(new DocumentUploadGrant(token, expiresAt));
    }
}

public static class UploadTokens
{
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
