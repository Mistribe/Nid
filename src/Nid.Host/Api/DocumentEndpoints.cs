using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using Nid.Core.Contracts;
using Nid.Documents.Application;
using Nid.Documents.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Host.Api.SubscriptionEndpoints;

namespace Nid.Host.Api;

/// <summary>Documents design §5, §7: upload, read, download, edit, archive, links and the scoped list.</summary>
public static class DocumentEndpoints
{
    /// <summary>#305: where an upload URL points; <see cref="Auth.CsrfMiddleware"/> leaves it alone, it reads no cookie.</summary>
    public const string UploadsPath = "/api/v1/documents/uploads";

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder routes)
    {
        // #305: no session and no token: the URL itself is the right, once, as whoever asked for it. The first request claims it, before a byte
        // is read, so a request with no valid URL reads no body and a refused file needs a new URL. Same answer for unknown, used or expired.
        routes.MapPost(UploadsPath + "/{token}", async (string token, HttpRequest request, DocumentsOptions options, IDocumentStore store,
                IPersonalAccessTokens tokens, IPersonRegistry persons, IActorAccessor actor, IInvoker invoker, IClock clock, CancellationToken ct) =>
            {
                var gone = Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "This upload URL is not valid: ask for a new one.");
                var now = clock.GetCurrentInstant();
                if (await store.ClaimUploadTicketAsync(UploadTokens.Hash(token), now, ct) is not { } ticket)
                    return gone;
                // As authentication refuses it: a token revoked since, an adult no longer active.
                var person = new PersonId(ticket.PersonId);
                var issuer = (await tokens.ListAsync(person, ct)).FirstOrDefault(t => t.Id == ticket.TokenId);
                if (issuer is null || issuer.RevokedAt is not null || issuer.ExpiresAt <= now
                    || await persons.FindAsync(person, ct) is not { Kind: PersonKind.Adult, IsPending: false })
                    return gone;

                if (await BoundedBodyAsync(request, options.MaxSizeBytes, ct) is not { } content)
                    return Result.Failure<UploadedDocument>(new Failure(DocumentsCodes.TooLarge, $"A document is at most {options.MaxSizeBytes} bytes.", null)).ToHttp(u => u);
                actor.Current = Actor.Token(person, issuer.Id, issuer.Scopes, ActorOrigin.Mcp);
                return (await invoker.InvokeAsync(new UploadDocumentCommand(content, ticket.FileName, ticket.Title, ticket.DocumentDate, ticket.Note,
                        new DocumentTarget(ticket.TargetKind, ticket.TargetId), ticket.Role, ticket.SourceRef, FromAssistant: true), ct))
                    .ToHttp(uploaded => uploaded, StatusCodes.Status201Created);
            })
            .AllowAnonymous()
            .Accepts<Stream>("application/octet-stream")
            .Returns<UploadedDocument>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        var api = routes.MapGroup("/api/v1/documents").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        // multipart/form-data, read by hand: the app has its own CSRF header, so no form binding (it would add antiforgery metadata).
        // targetKind=core.household needs no targetId.
        api.MapPost("", async (HttpRequest request, DocumentsOptions options, IInvoker invoker, CancellationToken ct) =>
            {
                if (!request.HasFormContentType || !request.ContentType!.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                    return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Upload a multipart/form-data body.");
                var tooLarge = Result.Failure<Guid>(new Failure(DocumentsCodes.TooLarge, $"A document is at most {options.MaxSizeBytes} bytes.", null))
                    .ToHttp(id => new IdResponse(id));
                // Review Focus 1: refused from the declared length, or while reading one sent with none; nothing is kept of it.
                if (request.ContentLength > options.MaxSizeBytes + FormSlack)
                    return tooLarge;
                // Kestrel's own limit (30 MB) is replaced, up or down, so a chunked body cannot carry several parts of the maximum size.
                if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                    limit.MaxRequestBodySize = options.MaxSizeBytes + FormSlack;

                IFormCollection form;
                try
                {
                    request.HttpContext.Features.Set<IFormFeature>(new FormFeature(request, new FormOptions { MultipartBodyLengthLimit = options.MaxSizeBytes }));
                    form = await request.ReadFormAsync(ct);
                }
                catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    return tooLarge;
                }
                catch (InvalidDataException e) when (e.Message.StartsWith("Multipart body length limit", StringComparison.Ordinal))
                {
                    return tooLarge;
                }
                catch (InvalidDataException)
                {
                    // A missing boundary or a broken part: the client's body, not the server.
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The multipart body cannot be read.");
                }

                string? Text(string name) => form.TryGetValue(name, out var v) && v.Count > 0 ? v.ToString() : null;
                if (form.Files.GetFile("file") is not { } file)
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The file part is required.");
                if (file.Length > options.MaxSizeBytes)
                    return tooLarge;

                var reader = new FieldReader();
                var documentDate = reader.Date(Text("date"), "date", required: false);
                if (!reader.Ok)
                    return reader.Problem();
                Guid? targetId = null;
                if (Text("targetId") is { } rawTarget)
                {
                    if (!Guid.TryParse(rawTarget, out var parsed))
                        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The targetId is not a valid identifier.");
                    targetId = parsed;
                }
                var targetKind = Text("targetKind");
                var link = targetKind is null && Text("targetId") is null ? null : new DocumentTarget(targetKind ?? "", targetId ?? TargetKinds.Household);

                await using var stream = file.OpenReadStream();
                var content = new byte[file.Length];
                await stream.ReadExactlyAsync(content, ct);
                var result = await invoker.InvokeAsync(new UploadDocumentCommand(content, file.FileName, Text("title"), documentDate, Text("note"), link,
                    Text("role"), WarnDuplicate: Text("warnDuplicate") == "true"), ct);
                // #297: the document already holding the file, for the page to offer linking it or adding the file anyway.
                return result.TryGetValue(out var uploaded) && uploaded!.Duplicate is { } duplicate
                    ? Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "This file is already in a document.",
                        extensions: new Dictionary<string, object?> { ["code"] = DocumentsCodes.Duplicate, ["duplicate"] = duplicate })
                    : result.ToHttp(created => new IdResponse(created.Id), StatusCodes.Status201Created);
            })
            .Accepts<UploadDocumentForm>("multipart/form-data")
            .Returns<IdResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        // Tranche 2 design §2.1: what the page may link a document to; module rows are linked from their own screen.
        api.MapGet("/targets", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new ListDocumentTargetsQuery(), ct)).ToHttp(list => list))
            .Returns<DocumentTargetList>();

        // Issue #414: the documents still missing on the items the actor writes (itemId: one item), and the actor's tax notice without one.
        api.MapGet("/expected", async (Guid? itemId, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new ListExpectedDocumentsQuery(itemId), ct)).ToHttp(list => list))
            .Returns<ExpectedDocumentList>();

        // Issue #414: « Pas disponible »: that document is no longer asked for.
        api.MapPost("/expected/dismissals", async (DismissExpectedBody body, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new DismissExpectedDocumentCommand(new DocumentTarget(body.TargetKind ?? "", body.TargetId), body.Role ?? "", body.Period), ct))
                    .ToHttp(x => new IdResponse(x)))
            .Returns<IdResponse>();

        // The readers a link would add, refused exactly as the link; targetKind=core.household needs no targetId.
        api.MapGet("/{id:guid}/links/preview", async (Guid id, string? targetKind, Guid? targetId, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new PreviewDocumentLinkQuery(id, new DocumentTarget(targetKind ?? "", targetId ?? TargetKinds.Household)), ct))
                    .ToHttp(preview => preview))
            .Returns<LinkPreview>();

        // Tax assessments spec §4: the adults among the declarants who would start reading the notice's PDF; writes nothing.
        api.MapGet("/{id:guid}/tax-notice-preview", async (Guid id, Guid[] declarants, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new PreviewTaxNoticeLinkQuery(id, declarants), ct)).ToHttp(preview => preview))
            .Returns<LinkPreview>();

        // inbox=true keeps the documents with no link the actor sees, whatever the scope; targetKind/targetId keep those linked to it.
        // anchor=<item> keeps those linked to the item or anything anchored on it, alone. Both or neither: only targetKind=core.household may come without a targetId (a targetId alone is a 400).
        api.MapGet("", async (string? scope, bool? archived, bool? inbox, string? targetKind, Guid? targetId, Guid? anchor, IInvoker invoker, CancellationToken ct)
                => anchor is not null && (inbox == true || targetKind is not null || targetId is not null)
                    ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Give anchor alone.")
                    : (targetKind is null) != (targetId is null) && !(targetKind == TargetKinds.CoreHousehold && targetId is null)
                    ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Give targetKind and targetId together.")
                    : (await invoker.InvokeAsync(new ListDocumentsQuery(ParseScope(scope), archived ?? false, inbox ?? false,
                        targetKind is null ? null : new DocumentTarget(targetKind, targetId ?? TargetKinds.Household), anchor), ct)).ToHttp(list => list))
            .Returns<DocumentList>();

        api.MapGet("/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetDocumentQuery(id), ct)).ToHttp(d => d))
            .Returns<DocumentDetail>();

        api.MapGet("/{id:guid}/content", async (Guid id, bool? inline, HttpContext http, IInvoker invoker, CancellationToken ct) =>
            {
                var result = await invoker.InvokeAsync(new GetDocumentContentQuery(id), ct);
                if (!result.TryGetValue(out var content))
                    return result.ToHttp(c => c.FileName); // the failure as Problem Details

                http.Response.Headers.XContentTypeOptions = "nosniff";
                http.Response.Headers.CacheControl = "private, no-store";
                // Issue #413: the in-app preview shows a PDF or an image inline; a mail is always a download.
                if (inline == true && InlineMediaTypes.Contains(content.MediaType))
                {
                    http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileNameStar = content.FileName }.ToString();
                    return Results.Stream(content.Content, content.MediaType);
                }
                // The framework writes Content-Disposition: attachment, the name quoted and RFC 5987-encoded (filename*).
                return Results.Stream(content.Content, content.MediaType, fileDownloadName: content.FileName);
            })
            .Produces(StatusCodes.Status200OK, typeof(Stream), "application/pdf", "image/png", "image/jpeg", "message/rfc822")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPut("/{id:guid}", async (Guid id, EditDocumentBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var documentDate = reader.Date(body.DocumentDate, "documentDate", required: false);
                return !reader.Ok
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new EditDocumentCommand(id, body.Title ?? "", documentDate, body.Note), ct)).ToHttp(x => new IdResponse(x));
            })
            .Returns<IdResponse>();

        // #305: a filing round, every line or none; dates are read here, so a refused one names its line.
        api.MapPost("/filing", async (FileDocumentsBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var edits = (body.Edits ?? []).OfType<DocumentEditBody>()
                    .Select((e, i) => new DocumentEdit(e.Id, e.Title, reader.Date(e.DocumentDate, $"edits[{i}].documentDate", required: false), e.Note)).ToList();
                var roles = (body.Roles ?? []).OfType<DocumentRoleBody>()
                    .Select(r => new DocumentRoleChange(r.Id, new DocumentTarget(r.TargetKind ?? "", r.TargetId ?? TargetKinds.Household), r.Role ?? "")).ToList();
                return !reader.Ok ? reader.Problem() : (await invoker.InvokeAsync(new FileDocumentsCommand(edits, roles), ct)).ToHttp(filed => filed);
            })
            .Returns<FiledDocuments>();

        api.MapPost("/{id:guid}/archive", async (Guid id, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new SetDocumentArchivedCommand(id, true), ct)).ToHttp(x => new IdResponse(x)))
            .Returns<IdResponse>();

        api.MapPost("/{id:guid}/unarchive", async (Guid id, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new SetDocumentArchivedCommand(id, false), ct)).ToHttp(x => new IdResponse(x)))
            .Returns<IdResponse>();

        // targetKind=core.household needs no targetId.
        api.MapPost("/{id:guid}/links", async (Guid id, LinkDocumentBody body, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(
                        new LinkDocumentCommand(id, new DocumentTarget(body.TargetKind ?? "", body.TargetId ?? TargetKinds.Household), body.Role ?? ""), ct))
                    .ToHttp(added => added))
            .Returns<LinkAdded>();

        api.MapPut("/{id:guid}/links/{targetKind}/{targetId:guid}", async (
                Guid id, string targetKind, Guid targetId, SetLinkRoleBody body, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new SetLinkRoleCommand(id, new DocumentTarget(targetKind, targetId), body.Role ?? ""), ct))
                    .ToHttp(x => new IdResponse(x)))
            .Returns<IdResponse>();

        api.MapDelete("/{id:guid}/links/{targetKind}/{targetId:guid}", async (Guid id, string targetKind, Guid targetId, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new UnlinkDocumentCommand(id, new DocumentTarget(targetKind, targetId)), ct)).ToHttp(x => new IdResponse(x)))
            .Returns<IdResponse>();

        return routes;
    }

    /// <summary>The body, at most <paramref name="max"/> bytes, declared or sent chunked; null past it, and nothing is kept.</summary>
    private static async Task<byte[]?> BoundedBodyAsync(HttpRequest request, long max, CancellationToken ct)
    {
        if (request.ContentLength > max)
            return null;
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = max;
        using var content = new MemoryStream();
        var chunk = new byte[81920];
        try
        {
            for (var read = await request.Body.ReadAsync(chunk, ct); read > 0; read = await request.Body.ReadAsync(chunk, ct))
            {
                if (content.Length + read > max)
                    return null;
                content.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }
        return content.ToArray();
    }

    /// <summary>Room for the form's other parts and boundaries on top of the file.</summary>
    private const long FormSlack = 64 * 1024;

    /// <summary>What a browser shows by itself (issue #413): a PDF and an image.</summary>
    private static readonly HashSet<string> InlineMediaTypes = ["application/pdf", "image/png", "image/jpeg"];

    /// <summary>The upload form, for the OpenAPI document only: the route reads the form itself.</summary>
    public sealed record UploadDocumentForm(
        IFormFile File, string? Title, string? Date, string? Note, string? TargetKind, Guid? TargetId, string? Role, bool? WarnDuplicate);

    public sealed record LinkDocumentBody(string? TargetKind, Guid? TargetId, string? Role);

    public sealed record SetLinkRoleBody(string? Role);

    /// <summary>Issue #414: a line of the expected documents; <c>period</c> 0 for a document expected once.</summary>
    public sealed record DismissExpectedBody(string? TargetKind, Guid TargetId, string? Role, int Period);

    /// <summary>Replaces the title, the date and the note; a missing title is refused by the handler.</summary>
    public sealed record EditDocumentBody(string? Title, string? DocumentDate, string? Note);

    /// <summary>A filing round (#305): the documents to change and the links whose role changes.</summary>
    public sealed record FileDocumentsBody(IReadOnlyList<DocumentEditBody>? Edits = null, IReadOnlyList<DocumentRoleBody>? Roles = null);

    /// <summary>Only what is given changes.</summary>
    public sealed record DocumentEditBody(Guid Id, string? Title = null, string? DocumentDate = null, string? Note = null);

    /// <summary>targetKind=core.household needs no targetId.</summary>
    public sealed record DocumentRoleBody(Guid Id, string? TargetKind, string? Role, Guid? TargetId = null);
}
