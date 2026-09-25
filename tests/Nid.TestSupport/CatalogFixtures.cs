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
    /// <summary>The merge of Mistribe/SubTracker-Data#5 (2026-09-25): the target format of 1C2b spec §8, no known defect.</summary>
    public const string PinnedCommit = "882b195d7923ada66993548adda10791e05d198a";

    /// <summary>1C2b's pinned commit (2025-11-29): colours in <c>#AARRGGBB</c>, <c>hosting</c> twice, no <c>home</c> label.</summary>
    public const string LegacyCommit = "df0146a8f3532f5c790aa54a34d4871e96b62a95";

    /// <summary>The files at <see cref="LegacyCommit"/>: what 1C2b's tests describe, and what the reader still tolerates.</summary>
    public static byte[] RealLabels() => File.ReadAllBytes(PathOf("labels.json"));

    public static byte[] RealProviders() => File.ReadAllBytes(PathOf("providers.json"));

    /// <summary>The files at <see cref="PinnedCommit"/>, fetched once into <c>tests/fixtures/catalog/pinned</c> (1C2c Task 8).</summary>
    public static byte[] PinnedLabels() => File.ReadAllBytes(PathOf(Path.Combine("pinned", "labels.json")));

    public static byte[] PinnedProviders() => File.ReadAllBytes(PathOf(Path.Combine("pinned", "providers.json")));

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
