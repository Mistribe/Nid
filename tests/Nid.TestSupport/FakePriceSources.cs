using Nid.Investments.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.TestSupport;

/// <summary>
/// Serves <see cref="Responses"/> by key (<c>boursorama-search:ISIN</c>, <c>boursorama:PATH</c>, <c>yahoo-search:ISIN</c>,
/// <c>yahoo-chart:SYMBOL</c>); any other key is a 404. Never touches the network.
/// </summary>
public sealed class FakePriceSources : IPriceSourceClient
{
    public Dictionary<string, byte[]> Responses { get; } = [];

    /// <summary>When set, every request fails with this code.</summary>
    public string? FailWith { get; set; }

    public List<string> Requested { get; } = [];

    /// <summary>What every test host uses unless a test installs its own: every request is unavailable.</summary>
    public static FakePriceSources Offline() => new() { FailWith = PriceCodes.SourceUnavailable };

    public Task<Result<PriceFetch>> BoursoramaSearchAsync(string isin, CancellationToken ct) => Serve($"boursorama-search:{isin}");

    public Task<Result<PriceFetch>> BoursoramaPageAsync(string path, CancellationToken ct) => Serve($"boursorama:{path}");

    public Task<Result<PriceFetch>> YahooSearchAsync(string isin, CancellationToken ct) => Serve($"yahoo-search:{isin}");

    public Task<Result<PriceFetch>> YahooChartAsync(string symbol, CancellationToken ct) => Serve($"yahoo-chart:{symbol}");

    private Task<Result<PriceFetch>> Serve(string key)
    {
        lock (Requested)
            Requested.Add(key);
        return Task.FromResult(FailWith is { } code
            ? Result.Failure<PriceFetch>(new Failure(code, "Fake source failure.", null))
            : Result.Success(new PriceFetch(Responses.GetValueOrDefault(key))));
    }
}
