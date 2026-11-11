using UnambitiousFx.Myelin;
using Nid.Subscriptions.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Subscriptions.Infrastructure.Catalog;

/// <summary>
/// The two files of a pinned commit over <see cref="SafeHttpClient"/> (design §4 "Source"): <c>labels.json</c> first,
/// then <c>providers.json</c>, 1 MB at most each. A file that must exist and does not is an unavailable source.
/// </summary>
public sealed class CatalogSourceClient(SafeHttpClient http, CatalogOptions options) : ICatalogSourceClient
{
    public async Task<Result<CatalogFiles>> FetchAsync(string commit, CancellationToken ct)
    {
        var labels = await FetchAsync(commit, "labels.json", ct);
        if (labels.TryGetFailure(out var labelsFailure))
            return Result.Failure<CatalogFiles>(labelsFailure!);
        var providers = await FetchAsync(commit, "providers.json", ct);
        if (providers.TryGetFailure(out var providersFailure))
            return Result.Failure<CatalogFiles>(providersFailure!);

        labels.TryGetValue(out var labelBytes);
        providers.TryGetValue(out var providerBytes);
        return Result.Success(new CatalogFiles(labelBytes!, providerBytes!));
    }

    public Uri UrlOf(string commit, string file) => new($"{options.BaseUrl.TrimEnd('/')}/{commit}/{file}");

    private async Task<Result<byte[]>> FetchAsync(string commit, string file, CancellationToken ct)
    {
        var url = UrlOf(commit, file);
        try
        {
            var response = await http.GetAsync(url, CatalogLimits.MaxFileBytes, ct);
            if ((int)response.Status is < 200 or > 299)
                return Fail(CatalogCodes.SourceUnavailable, $"{url.Host} answered HTTP {(int)response.Status} for {file}.");
            return Result.Success(response.Body);
        }
        catch (OutboundRefusedException refused) when (refused.Reason == RefusalReasons.TooLarge)
        {
            return Fail(CatalogCodes.SourceMalformed, $"{file} is larger than {CatalogLimits.MaxFileBytes} bytes.");
        }
        catch (OutboundRefusedException refused)
        {
            return Fail(CatalogCodes.SourceRefused, refused.Message);
        }
        catch (Exception e) when (e is HttpRequestException or IOException
                                   || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return Fail(CatalogCodes.SourceUnavailable, $"{url.Host} could not be reached ({e.GetType().Name}).");
        }
    }

    private static Result<byte[]> Fail(string code, string message) => Result.Failure<byte[]>(new Failure(code, message, null));
}
