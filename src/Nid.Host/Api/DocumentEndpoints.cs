using Microsoft.AspNetCore.Http.Features;
using Nid.Documents.Application;
using Nid.Documents.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Host.Api.SubscriptionEndpoints;

namespace Nid.Host.Api;

/// <summary>Documents design §5, §7: upload, read, download, edit, archive, links and the scoped list.</summary>
public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder routes)
    {
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
                return (await invoker.InvokeAsync(
                        new UploadDocumentCommand(content, file.FileName, Text("title"), documentDate, Text("note"), link, Text("role")), ct))
                    .ToHttp(id => new IdResponse(id), StatusCodes.Status201Created);
            })
            .Accepts<UploadDocumentForm>("multipart/form-data")
            .Returns<IdResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        // Tranche 2 design §2.1: what the page may link a document to; module rows are linked from their own screen.
        api.MapGet("/targets", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new ListDocumentTargetsQuery(), ct)).ToHttp(list => list))
            .Returns<DocumentTargetList>();

        // The readers a link would add, refused exactly as the link; targetKind=core.household needs no targetId.
        api.MapGet("/{id:guid}/links/preview", async (Guid id, string? targetKind, Guid? targetId, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new PreviewDocumentLinkQuery(id, new DocumentTarget(targetKind ?? "", targetId ?? TargetKinds.Household)), ct))
                    .ToHttp(preview => preview))
            .Returns<LinkPreview>();

        // inbox=true keeps the documents with no link the actor sees, whatever the scope; targetKind/targetId keep those linked to it.
        // Both or neither: only targetKind=core.household may come without a targetId (a targetId alone is a 400).
        api.MapGet("", async (string? scope, bool? archived, bool? inbox, string? targetKind, Guid? targetId, IInvoker invoker, CancellationToken ct)
                => (targetKind is null) != (targetId is null) && !(targetKind == TargetKinds.CoreHousehold && targetId is null)
                    ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Give targetKind and targetId together.")
                    : (await invoker.InvokeAsync(new ListDocumentsQuery(ParseScope(scope), archived ?? false, inbox ?? false,
                        targetKind is null ? null : new DocumentTarget(targetKind, targetId ?? TargetKinds.Household)), ct)).ToHttp(list => list))
            .Returns<DocumentList>();

        api.MapGet("/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetDocumentQuery(id), ct)).ToHttp(d => d))
            .Returns<DocumentDetail>();

        api.MapGet("/{id:guid}/content", async (Guid id, HttpContext http, IInvoker invoker, CancellationToken ct) =>
            {
                var result = await invoker.InvokeAsync(new GetDocumentContentQuery(id), ct);
                if (!result.TryGetValue(out var content))
                    return result.ToHttp(c => c.FileName); // the failure as Problem Details

                http.Response.Headers.XContentTypeOptions = "nosniff";
                http.Response.Headers.CacheControl = "private, no-store";
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

    /// <summary>Room for the form's other parts and boundaries on top of the file.</summary>
    private const long FormSlack = 64 * 1024;

    /// <summary>The upload form, for the OpenAPI document only: the route reads the form itself.</summary>
    public sealed record UploadDocumentForm(IFormFile File, string? Title, string? Date, string? Note, string? TargetKind, Guid? TargetId, string? Role);

    public sealed record LinkDocumentBody(string? TargetKind, Guid? TargetId, string? Role);

    public sealed record SetLinkRoleBody(string? Role);

    /// <summary>Replaces the title, the date and the note; a missing title is refused by the handler.</summary>
    public sealed record EditDocumentBody(string? Title, string? DocumentDate, string? Note);
}
