using Microsoft.Extensions.Logging;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;

namespace Nid.Subscriptions.Application.Catalog;

/// <summary>
/// 1C2c design §5, step 2 of a run, after the merge committed (nothing is downloaded under its lock): every live
/// catalog provider whose icon is missing or came from another URL, one at a time, in key order — download and
/// process (<see cref="ICatalogIconSource"/>), store (<see cref="IAssetStore"/>), link (one guarded update). A failure
/// never fails the run, whether it is a coded refusal or an exception no one foresaw: the provider keeps the icon it
/// had, or its monogram, and <c>icon_source</c> still differing from <c>icon_url</c> makes the next sync retry it.
/// Only the run's own cancellation stops the pass (review follow-up, Important 1).
/// </summary>
public sealed partial class CatalogIconPass(
    IProviderStore providers, ICatalogIconSource icons, IAssetStore assets, ILogger<CatalogIconPass> logger)
{
    /// <param name="readerRefused">The catalog reader's own icon refusals (design §5), filtered below.</param>
    /// <param name="conflictKeys">
    /// Catalog keys the merge blocked with <see cref="CatalogCodes"/> "conflicts" (a custom provider of the same key):
    /// they never became catalog providers this sync, so a reader refusal naming one is not this pass's to report
    /// (review follow-up, Minor 2).
    /// </param>
    public async Task<IconReport> RunAsync(IReadOnlyList<IconRefusal> readerRefused, IReadOnlyCollection<string> conflictKeys, CancellationToken ct)
    {
        var fetched = new List<string>();
        var refused = new List<IconRefusal>(readerRefused.Where(r => !conflictKeys.Contains(r.Key)));
        var unchanged = 0;

        IReadOnlyList<Provider> candidates;
        try
        {
            candidates = await providers.CatalogIconsAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Review follow-up, Important 1: the store itself could not be read; nothing can be attempted this run.
            // The run still succeeds — the reader's own refusals above still count, and the next sync tries again.
            LogPassFailed(e);
            return new IconReport(fetched, unchanged, refused.OrderBy(r => r.Key, StringComparer.Ordinal).ToList());
        }

        foreach (var provider in candidates)
        {
            var url = provider.IconUrl!;
            if (provider.IconAsset is not null && provider.IconSource == url)
            {
                unchanged++;
                continue;
            }

            ct.ThrowIfCancellationRequested();
            var storing = false;
            try
            {
                var icon = await icons.FetchAsync(url, ct);
                if (icon.TryGetFailure(out var failure))
                {
                    refused.Add(new IconRefusal(provider.Key, failure!.Code));
                    continue;
                }
                icon.TryGetValue(out var processed);

                storing = true;
                var hash = await assets.PutAsync(processed!.Webp, IconLimits.ContentType, ct);
                // Review follow-up, Minor 1: 0 rows means the provider's icon URL moved on again while this pass was
                // running; nothing was linked, icon_source still differs from icon_url, and the next sync retries it.
                if (await providers.SetIconAsync(provider.Key, url, hash, ct) == 1)
                    fetched.Add(provider.Key);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Review follow-up, Important 1: an exception no one foresaw — from the source, the store or the
                // link — is this icon's refusal, never a failed run; the pass moves on to the next provider.
                refused.Add(new IconRefusal(provider.Key, storing ? IconCodes.StoreFailed : IconCodes.Unreachable));
                LogIconFailed(provider.Key, e);
            }
        }

        ct.ThrowIfCancellationRequested(); // Review follow-up, Minor 3
        return new IconReport(fetched, unchanged, refused.OrderBy(r => r.Key, StringComparer.Ordinal).ToList());
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The icon of provider {Key} could not be fetched, stored or linked")]
    private partial void LogIconFailed(string key, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The icon pass could not read the catalog's providers")]
    private partial void LogPassFailed(Exception exception);
}
