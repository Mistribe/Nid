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

        // The body is the file itself; everything else is in the query. targetKind=core.household needs no targetId.
        api.MapPost("", async (
                HttpRequest request, string? fileName, string? title, string? date, string? note, string? targetKind, Guid? targetId,
                string? role, DocumentsOptions options, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var documentDate = reader.Date(date, "date", required: false);
                if (!reader.Ok)
                    return reader.Problem();
                if (await ReadBoundedAsync(request, options.MaxSizeBytes, ct) is not { } content)
                    return Result.Failure<Guid>(new Failure(DocumentsCodes.TooLarge, $"A document is at most {options.MaxSizeBytes} bytes.", null))
                        .ToHttp(id => new IdResponse(id));

                var link = targetKind is null && targetId is null ? null : new DocumentTarget(targetKind ?? "", targetId ?? TargetKinds.Household);
                return (await invoker.InvokeAsync(new UploadDocumentCommand(content, fileName, title, documentDate, note, link, role), ct))
                    .ToHttp(id => new IdResponse(id), StatusCodes.Status201Created);
            })
            .Accepts<Stream>("application/octet-stream")
            .Returns<IdResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        // inbox=true keeps the documents with no link the actor sees, whatever the scope.
        api.MapGet("", async (string? scope, bool? archived, bool? inbox, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new ListDocumentsQuery(ParseScope(scope), archived ?? false, inbox ?? false), ct)).ToHttp(list => list))
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

    public sealed record LinkDocumentBody(string? TargetKind, Guid? TargetId, string? Role);

    public sealed record SetLinkRoleBody(string? Role);

    /// <summary>Replaces the title, the date and the note; a missing title is refused by the handler.</summary>
    public sealed record EditDocumentBody(string? Title, string? DocumentDate, string? Note);

    /// <summary>
    /// Review Focus 1: the whole body, or <c>null</c> as soon as it is known to exceed <paramref name="max"/> — from its declared length,
    /// or while reading one sent with none. Nothing is kept of a refused body.
    /// </summary>
    private static async Task<ReadOnlyMemory<byte>?> ReadBoundedAsync(HttpRequest request, long max, CancellationToken ct)
    {
        if (request.ContentLength > max)
            return null;
        // Kestrel refuses bodies past its own limit (30 MB) on its own; a larger configured maximum lifts it for this request.
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit && limit.MaxRequestBodySize < max + 1)
            limit.MaxRequestBodySize = max + 1;

        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > max)
                    return null;
                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException tooLarge) when (tooLarge.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }

        return buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
    }
}
