using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;

namespace Nid.Subscriptions.Application.Catalog;

/// <summary>
/// 1C2c design §5, step 2 of a run, after the merge committed (nothing is downloaded under its lock): every live
/// catalog provider whose icon is missing or came from another URL, one at a time, in key order — download and
/// process (<see cref="ICatalogIconSource"/>), store (<see cref="IAssetStore"/>), link (one guarded update). A failure
/// never fails the run: the provider keeps the icon it had, or its monogram, and <c>icon_source</c> still differing
/// from <c>icon_url</c> makes the next sync retry it. A cancellation stops the pass at once.
/// </summary>
public sealed class CatalogIconPass(IProviderStore providers, ICatalogIconSource icons, IAssetStore assets)
{
    public async Task<IconReport> RunAsync(IReadOnlyList<IconRefusal> readerRefused, CancellationToken ct)
    {
        var fetched = new List<string>();
        var refused = new List<IconRefusal>(readerRefused);
        var unchanged = 0;

        foreach (var provider in await providers.CatalogIconsAsync(ct))
        {
            var url = provider.IconUrl!;
            if (provider.IconAsset is not null && provider.IconSource == url)
            {
                unchanged++;
                continue;
            }

            ct.ThrowIfCancellationRequested();
            var icon = await icons.FetchAsync(url, ct);
            if (icon.TryGetFailure(out var failure))
            {
                refused.Add(new IconRefusal(provider.Key, failure!.Code));
                continue;
            }
            icon.TryGetValue(out var processed);

            string hash;
            try
            {
                hash = await assets.PutAsync(processed!.Webp, IconLimits.ContentType, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                refused.Add(new IconRefusal(provider.Key, IconCodes.StoreFailed)); // Review Focus 5
                continue;
            }

            await providers.SetIconAsync(provider.Key, url, hash, ct);
            fetched.Add(provider.Key);
        }

        return new IconReport(fetched, unchanged, refused.OrderBy(r => r.Key, StringComparer.Ordinal).ToList());
    }
}
