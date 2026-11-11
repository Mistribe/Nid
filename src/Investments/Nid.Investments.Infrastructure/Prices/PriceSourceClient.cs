using System.Net;
using Nid.Investments.Contracts;
using UnambitiousFx.Myelin;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Investments.Infrastructure.Prices;

public sealed class PriceSourceClient(SafeHttpClient http, PriceOptions options) : IPriceSourceClient
{
    public const int MaxBytes = 3 * 1024 * 1024;

    public Task<Result<PriceFetch>> BoursoramaSearchAsync(string isin, CancellationToken ct)
        => FetchAsync($"{options.BoursoramaBaseUrl}/recherche/ajax?query={Uri.EscapeDataString(isin)}", ct);

    /// <summary>The path comes from a third-party page: it must stay on the Boursorama host, not replace it (<c>//host/x</c>, <c>https://host/x</c>).</summary>
    public Task<Result<PriceFetch>> BoursoramaPageAsync(string path, CancellationToken ct)
        => !path.StartsWith('/') || path.StartsWith("//") || !Uri.TryCreate(new Uri(options.BoursoramaBaseUrl), path, out var url)
           || !url.Host.Equals(new Uri(options.BoursoramaBaseUrl).Host, StringComparison.OrdinalIgnoreCase)
            ? Task.FromResult(Fail(PriceCodes.SourceMalformed, "Boursorama page path is not a path of its host."))
            : FetchAsync(url.AbsoluteUri, ct);

    public Task<Result<PriceFetch>> YahooSearchAsync(string isin, CancellationToken ct)
        => FetchAsync($"{options.YahooBaseUrl}/v1/finance/search?q={Uri.EscapeDataString(isin)}&quotesCount=3&newsCount=0", ct);

    /// <summary>A symbol of only dots would be a dot segment, resolved away from the chart path.</summary>
    public Task<Result<PriceFetch>> YahooChartAsync(string symbol, CancellationToken ct)
        => symbol.Trim('.').Length == 0
            ? Task.FromResult(Fail(PriceCodes.SourceMalformed, "Yahoo symbol is empty."))
            : FetchAsync($"{options.YahooBaseUrl}/v8/finance/chart/{Uri.EscapeDataString(symbol)}?range=5d&interval=1d", ct);

    /// <summary>A 404 is an unknown page, not a failure: the collector tries the next source.</summary>
    private async Task<Result<PriceFetch>> FetchAsync(string address, CancellationToken ct)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url))
            return Fail(PriceCodes.SourceMalformed, "The source URL is not valid.");
        try
        {
            var response = await http.GetAsync(url, MaxBytes, ct);
            if (response.Status == HttpStatusCode.NotFound)
                return Result.Success(new PriceFetch(null));
            if ((int)response.Status is < 200 or > 299)
                return Fail(PriceCodes.SourceUnavailable, $"{url.Host} answered HTTP {(int)response.Status}.");
            return Result.Success(new PriceFetch(response.Body));
        }
        catch (OutboundRefusedException refused) when (refused.Reason == RefusalReasons.TooLarge)
        {
            return Fail(PriceCodes.SourceMalformed, $"{url.Host} sent more than {MaxBytes} bytes.");
        }
        catch (OutboundRefusedException refused)
        {
            return Fail(PriceCodes.SourceRefused, refused.Message);
        }
        catch (Exception e) when (e is HttpRequestException or IOException
                                   || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return Fail(PriceCodes.SourceUnavailable, $"{url.Host} could not be reached ({e.GetType().Name}).");
        }
    }

    private static Result<PriceFetch> Fail(string code, string message) => Result.Failure<PriceFetch>(new Failure(code, message, null));
}
