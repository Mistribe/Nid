using System.Text;
using System.Text.Json;
using Nid.Subscriptions.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.TestSupport;

/// <summary>
/// The two files of <c>Mistribe/SubTracker-Data</c> at the pinned commit, fetched once into <c>tests/fixtures/catalog</c>
/// (1C2b Task 2) and copied next to the test assembly that reads them.
/// </summary>
public static class CatalogFixtures
{
    public const string PinnedCommit = "df0146a8f3532f5c790aa54a34d4871e96b62a95";

    public static byte[] RealLabels() => File.ReadAllBytes(PathOf("labels.json"));

    public static byte[] RealProviders() => File.ReadAllBytes(PathOf("providers.json"));

    private static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "catalog", name);

    /// <summary>One label, <c>streaming</c>, in the target format.</summary>
    public static byte[] StreamingLabels() => Encoding.UTF8.GetBytes("""[{"key":"streaming","name":"Streaming","color":"#6400F6"}]""");

    /// <summary>A <c>providers.json</c> of the given entries, all in <c>streaming</c>; a null icon is written as <c>"icon": null</c>.</summary>
    public static byte[] Providers(params (string Key, string Name, string? Icon)[] entries)
        => JsonSerializer.SerializeToUtf8Bytes(entries.Select(e => new Dictionary<string, object?>
        {
            ["key"] = e.Key,
            ["name"] = e.Name,
            ["categories"] = new[] { "streaming" },
            ["icon"] = e.Icon,
        }).ToList());
}

/// <summary>Serves <see cref="Files"/> for any commit, or fails with <see cref="FailWith"/>. Never touches the network.</summary>
public sealed class FakeCatalogSource : ICatalogSourceClient
{
    public CatalogFiles? Files { get; set; }

    /// <summary>When set, every fetch fails with this code.</summary>
    public string? FailWith { get; set; }

    /// <summary>The commits asked for, in order.</summary>
    public List<string> Requested { get; } = [];

    /// <summary>Runs before the answer, with the caller's token: lets a test hold a fetch open or cancel it.</summary>
    public Func<CancellationToken, Task>? Before { get; set; }

    /// <summary>What every test host uses unless a test serves files: every fetch is unavailable.</summary>
    public static FakeCatalogSource Offline() => new() { FailWith = CatalogCodes.SourceUnavailable };

    public void Serve(byte[] labels, byte[] providers)
    {
        FailWith = null;
        Files = new CatalogFiles(labels, providers);
    }

    public async Task<Result<CatalogFiles>> FetchAsync(string commit, CancellationToken ct)
    {
        lock (Requested)
            Requested.Add(commit);
        if (Before is { } before)
            await before(ct);
        return FailWith is { } code
            ? Result.Failure<CatalogFiles>(new Failure(code, "Fake catalog failure.", null))
            : Files is { } files
                ? Result.Success(files)
                : Result.Failure<CatalogFiles>(new Failure(CatalogCodes.SourceUnavailable, "The fake serves no files.", null));
    }
}
