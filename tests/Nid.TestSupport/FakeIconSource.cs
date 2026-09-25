using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Infrastructure.Catalog;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.TestSupport;

/// <summary>
/// Serves the bytes given for a URL — through the real <see cref="IconProcessor"/>, so a test exercises the whole
/// processing — or fails with the reason given. Any other URL is <c>unreachable</c>. Never touches the network.
/// </summary>
public sealed class FakeIconSource : ICatalogIconSource
{
    private readonly Dictionary<string, byte[]> images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> failures = new(StringComparer.Ordinal);

    /// <summary>The URLs asked for, in order.</summary>
    public List<string> Requested { get; } = [];

    /// <summary>Runs before the answer, with the URL and the caller's token: lets a test cancel the pass mid-way.</summary>
    public Func<string, CancellationToken, Task>? Before { get; set; }

    /// <summary>What every test host uses unless a test serves icons: every fetch is unreachable.</summary>
    public static FakeIconSource Offline() => new();

    public void Serve(string url, byte[] image)
    {
        lock (images)
        {
            failures.Remove(url);
            images[url] = image;
        }
    }

    public void Fail(string url, string reason)
    {
        lock (images)
        {
            images.Remove(url);
            failures[url] = reason;
        }
    }

    public async Task<Result<ProcessedIcon>> FetchAsync(string url, CancellationToken ct)
    {
        lock (Requested)
            Requested.Add(url);
        if (Before is { } before)
            await before(url, ct);

        byte[]? image;
        string? reason;
        lock (images)
        {
            images.TryGetValue(url, out image);
            failures.TryGetValue(url, out reason);
        }

        return image is not null
            ? IconProcessor.Process(image)
            : Result.Failure<ProcessedIcon>(new Failure(reason ?? IconCodes.Unreachable, "Fake icon failure.", null));
    }
}
