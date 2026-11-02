using System.Security.Cryptography;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Assets;

namespace Nid.Platform.Tests;

/// <summary>1C2c design §3, against a temporary folder of its own per test.</summary>
public sealed class FileAssetStoreTests : IDisposable
{
    private const string Webp = FileAssetStore.WebpContentType;

    private readonly string root = Path.Combine(Path.GetTempPath(), "nid-assets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private FileAssetStore Store(string? at = null) => new(new AssetOptions { Root = at ?? root });

    private static string HashOf(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private string[] FilesUnderRoot() => Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories) : [];

    [Fact]
    public async Task An_asset_is_stored_under_its_hash_in_a_folder_named_by_its_first_two_characters()
    {
        byte[] content = [1, 2, 3, 4];

        var hash = await Store().PutAsync(content, Webp, TestContext.Current.CancellationToken);

        Assert.Equal(HashOf(content), hash);
        var file = Path.Combine(root, hash[..2], hash + ".webp");
        Assert.Equal(content, await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
        Assert.Equal([file], FilesUnderRoot()); // no temporary file left behind
    }

    [Fact]
    public async Task The_same_content_written_twice_is_written_once()
    {
        byte[] content = [5, 6, 7];
        var store = Store();
        var hash = await store.PutAsync(content, Webp, TestContext.Current.CancellationToken);
        var file = Path.Combine(root, hash[..2], hash + ".webp");
        var marked = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, marked);

        Assert.Equal(hash, await store.PutAsync(content, Webp, TestContext.Current.CancellationToken));

        Assert.Equal(marked, File.GetLastWriteTimeUtc(file)); // not rewritten
    }

    /// <summary>Review Focus 5: two providers with the same icon, or two writers racing on one hash.</summary>
    [Fact]
    public async Task Concurrent_writes_of_the_same_content_leave_one_file_and_no_temporary()
    {
        var content = RandomNumberGenerator.GetBytes(256 * 1024);

        var hashes = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => Store().PutAsync(content, Webp, default))));

        var hash = Assert.Single(hashes.Distinct());
        var file = Assert.Single(FilesUnderRoot());
        Assert.Equal(Path.Combine(root, hash[..2], hash + ".webp"), file);
        Assert.Equal(content, await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_cancelled_write_leaves_neither_the_asset_nor_a_temporary_file()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().PutAsync(new byte[4096], Webp, cancelled.Token));

        Assert.Empty(FilesUnderRoot());
    }

    [Fact]
    public async Task A_stored_asset_reads_back()
    {
        byte[] content = [9, 8, 7];
        var store = Store();
        var hash = await store.PutAsync(content, Webp, TestContext.Current.CancellationToken);

        await using var stream = await store.OpenAsync(hash, TestContext.Current.CancellationToken);

        Assert.NotNull(stream);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
        Assert.Equal(content, copy.ToArray());
    }

    [Fact]
    public async Task A_well_formed_hash_of_nothing_answers_null()
        => Assert.Null(await Store().OpenAsync(HashOf([42]), TestContext.Current.CancellationToken));

    public static TheoryData<string> Malformed => new()
    {
        "",
        "abc",
        new string('A', 64),               // uppercase is not the store's spelling
        new string('a', 63) + "g",
        new string('a', 65),
        new string('a', 64) + "\n",
        "../" + new string('a', 61),
        "..\\" + new string('a', 61),
        "/etc/" + new string('a', 59),
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task A_malformed_hash_answers_null_without_touching_the_disk(string hash)
    {
        Assert.Null(await Store().OpenAsync(hash, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(root)); // not even the root was created or looked into
    }

    /// <summary>Review Focus 5: a volume mounted read-only, or a root that is a file.</summary>
    [Fact]
    public async Task An_unwritable_root_refuses_the_write_with_an_IOException()
    {
        Directory.CreateDirectory(root);
        var blocker = Path.Combine(root, "blocker");
        await File.WriteAllTextAsync(blocker, "a file where a folder should be", TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<IOException>(() => Store(Path.Combine(blocker, "assets")).PutAsync(new byte[] { 1 }, Webp, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Only_webp_is_stored()
        => await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Store().PutAsync(new byte[] { 1 }, "image/png", TestContext.Current.CancellationToken));
}
