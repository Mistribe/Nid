using Microsoft.Extensions.Primitives;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using UnambitiousFx.Synapse.Abstractions;
using EntityTag = Microsoft.Net.Http.Headers.EntityTagHeaderValue;

namespace Nid.Host.Api;

/// <summary>Design §5 "Endpoints": providers, labels and the catalog journal, under the subscriptions module.</summary>
public static class CatalogEndpoints
{
    /// <summary>A custom provider as the browser sends it; the key is generated, never sent (design §3.3).</summary>
    public sealed record CreateProviderBody(string Name, string? Website, string? PricingPage, IReadOnlyList<string>? LabelKeys);

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/subscriptions").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("/providers", async (string? q, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new ListProvidersQuery(RequireSearch(q)), ct)).ToHttp(r => r))
            .Returns<IReadOnlyList<ProviderView>>();

        api.MapPost("/providers", async (CreateProviderBody body, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(
                        new CreateProviderCommand(body.Name, body.Website, body.PricingPage, RequireLabelKeys(body.LabelKeys)), ct))
                    .ToHttp(r => r, StatusCodes.Status201Created))
            .Returns<ProviderView>(StatusCodes.Status201Created);

        api.MapGet("/labels", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new ListLabelsQuery(), ct)).ToHttp(r => r))
            .Returns<IReadOnlyList<LabelView>>();

        api.MapGet("/catalog/runs", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetCatalogRunsQuery(), ct)).ToHttp(r => r))
            .Returns<CatalogRunsView>();

        api.MapPost("/catalog/sync", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new RequestCatalogSyncCommand(), ct)).ToHttp(r => r, StatusCodes.Status202Accepted))
            .Returns<CatalogSyncRequested>(StatusCodes.Status202Accepted);

        api.MapGet("/providers/icons/{hash}", async (string hash, HttpContext http, IInvoker invoker, CancellationToken ct) =>
            {
                var result = await invoker.InvokeAsync(new GetProviderIconQuery(hash), ct);
                if (!result.TryGetValue(out var icon))
                    return result.ToHttp(i => i); // the 404 as Problem Details

                // Design §6: content-addressed, so cached for a year and never revalidated before; the ETag is the hash.
                var headers = http.Response.Headers;
                headers.ETag = $"\"{icon.Hash}\"";
                headers.CacheControl = IconLimits.CacheControl;
                headers.XContentTypeOptions = "nosniff";
                return Names(http.Request.Headers.IfNoneMatch, icon.Hash)
                    ? Results.StatusCode(StatusCodes.Status304NotModified)
                    : Results.Bytes(icon.Content, IconLimits.ContentType);
            })
            .Produces(StatusCodes.Status200OK, typeof(Stream), IconLimits.ContentType)
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }

    /// <summary>Blank stays blank (no filter); nothing longer than a provider name could ever match one, so longer is refused, not searched.</summary>
    private static string? RequireSearch(string? q)
        => q is { Length: > CatalogLimits.MaxNameLength }
            ? throw new BadHttpRequestException($"'q' is at most {CatalogLimits.MaxNameLength} characters.")
            : q;

    /// <summary>
    /// Absent is an empty list, which <see cref="CreateProviderCommand"/> already refuses as "no label" (422
    /// subscriptions.invalid_provider). A null entry inside the array is never a valid label key and would
    /// otherwise reach the store as a null element of a text[] column (a 500) — refused here as malformed
    /// input (400 request.malformed) before the command ever runs (Task 6 review).
    /// </summary>
    private static IReadOnlyList<string> RequireLabelKeys(IReadOnlyList<string>? labelKeys)
        => labelKeys is null
            ? []
            : labelKeys.Any(key => key is null)
                ? throw new BadHttpRequestException("A label key is required for each entry of 'labelKeys'.")
                : labelKeys;

    /// <summary>RFC 9110 §13.1.2: <c>If-None-Match</c> is compared weakly; <c>*</c> matches any current version.</summary>
    private static bool Names(StringValues ifNoneMatch, string hash)
        => EntityTag.TryParseList(ifNoneMatch, out var tags)
           && tags!.Any(tag => tag.Equals(EntityTag.Any) || tag.Tag.Equals($"\"{hash}\""));
}
