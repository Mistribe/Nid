using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Subscriptions.Application.Catalog;

/// <summary><c>GET /providers/icons/{hash}</c> (1C2c design §6): open to every adult, as the provider list is.</summary>
public sealed record GetProviderIconQuery(string Hash) : IQuery<ProviderIcon>;

/// <summary>An icon's bytes (a few tens of KB at most): bytes, not a stream, so every read can be serialised (the leak bench does).</summary>
public sealed record ProviderIcon(string Hash, byte[] Content);

[RequestHandler<GetProviderIconQuery, ProviderIcon>]
public sealed class GetProviderIconHandler(IProviderStore providers, IAssetStore assets) : IRequestHandler<GetProviderIconQuery, ProviderIcon>
{
    public async ValueTask<Result<ProviderIcon>> HandleAsync(GetProviderIconQuery query, CancellationToken ct = default)
    {
        // Malformed, unknown, not a catalog provider's, or missing on the volume: the same 404, so this route can never
        // serve anything else the asset store holds (stage 4 documents) nor tell the cases apart.
        if (!AssetHash.IsValid(query.Hash) || !await providers.IsCatalogIconAsync(query.Hash, ct))
            return NotFound();

        await using var stream = await assets.OpenAsync(query.Hash, ct);
        if (stream is null)
            return NotFound();

        using var content = new MemoryStream();
        await stream.CopyToAsync(content, ct);
        return Result.Success(new ProviderIcon(query.Hash, content.ToArray()));
    }

    private static Result<ProviderIcon> NotFound() => Result.Failure<ProviderIcon>(new NotFoundFailure("provider icon", "requested"));
}
