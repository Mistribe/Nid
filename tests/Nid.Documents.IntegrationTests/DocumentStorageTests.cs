using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Nid.Documents.Application;
using Nid.Documents.Infrastructure.Storage;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Nid.TestSupport;

namespace Nid.Documents.IntegrationTests;

/// <summary>One MinIO container for the run, like <c>PostgresFixture</c>: Testcontainers' reaper removes it.</summary>
public sealed class MinioFixture : IAsyncLifetime
{
    private const string User = "minioadmin", Password = "minioadmin";

    // minio/minio is no longer published on Docker Hub or quay.io; Chainguard's free tier only ships :latest.
    // A plain container: Testcontainers.Minio stopped at 4.15 and breaks against newer Testcontainers.
    private static readonly IContainer Container = new ContainerBuilder("cgr.dev/chainguard/minio:latest")
        .WithPortBinding(9000, true)
        .WithEnvironment("MINIO_ROOT_USER", User)
        .WithEnvironment("MINIO_ROOT_PASSWORD", Password)
        .WithCommand("server", "/data")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPath("/minio/health/ready").ForPort(9000)))
        .Build();
    private static readonly Lazy<Task> Started = new(() => Container.StartAsync());

    public ValueTask InitializeAsync() => new(Started.Value);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static AmazonS3Client NewClient(DocumentStorageOptions options) => new(
        new BasicAWSCredentials(options.AccessKey, options.SecretKey),
        new AmazonS3Config { ServiceURL = options.ServiceUrl, ForcePathStyle = true });

    public async Task<DocumentStorageOptions> NewBucketAsync()
    {
        await Started.Value;
        var options = new DocumentStorageOptions
        {
            Kind = "s3", ServiceUrl = $"http://{Container.Hostname}:{Container.GetMappedPublicPort(9000)}", Bucket = $"docs-{Guid.NewGuid():N}",
            AccessKey = User, SecretKey = Password,
        };
        using var client = NewClient(options);
        await client.PutBucketAsync(options.Bucket);
        return options;
    }
}

[CollectionDefinition("minio")]
public sealed class MinioCollection : ICollectionFixture<MinioFixture>;

[Collection("minio")]
public sealed class DocumentStorageTests(MinioFixture minio) : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "nid-docs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    public static TheoryData<string> Kinds => ["filesystem", "s3"];

    private async Task<(IDocumentStorage Storage, Func<int> Count)> NewAsync(string kind)
    {
        if (kind == "filesystem")
            return (new FileDocumentStorage(new DocumentStorageOptions { Path = root }),
                () => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count());

        var options = await minio.NewBucketAsync();
        var s3 = new S3DocumentStorage(options);
        return (s3, () =>
        {
            using var client = MinioFixture.NewClient(options);
            return client.ListObjectsV2Async(new() { BucketName = options.Bucket }).GetAwaiter().GetResult().KeyCount ?? 0;
        });
    }

    private static async Task<byte[]> ReadAsync(Stream stream)
    {
        await using var s = stream;
        using var buffer = new MemoryStream();
        await s.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task Put_then_open_returns_the_same_bytes(string kind)
    {
        var (storage, _) = await NewAsync(kind);
        var bytes = RandomNumberGenerator.GetBytes(2048);

        var hash = await storage.PutAsync(bytes, CancellationToken.None);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), hash);
        var stream = await storage.OpenAsync(hash, CancellationToken.None);
        Assert.NotNull(stream);
        Assert.Equal(bytes, await ReadAsync(stream));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task Putting_the_same_bytes_twice_leaves_one_object(string kind)
    {
        var (storage, count) = await NewAsync(kind);
        var bytes = RandomNumberGenerator.GetBytes(512);

        var first = await storage.PutAsync(bytes, CancellationToken.None);
        var second = await storage.PutAsync(bytes, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(1, count());
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task A_malformed_or_unknown_hash_opens_nothing(string kind)
    {
        var (storage, _) = await NewAsync(kind);

        Assert.Null(await storage.OpenAsync("../x", CancellationToken.None));
        Assert.Null(await storage.OpenAsync(new string('a', 64), CancellationToken.None));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task Delete_removes_the_bytes_and_ignores_a_missing_or_malformed_hash(string kind)
    {
        var (storage, count) = await NewAsync(kind);
        var hash = await storage.PutAsync(RandomNumberGenerator.GetBytes(256), CancellationToken.None);

        await storage.DeleteAsync(hash, CancellationToken.None);
        await storage.DeleteAsync(hash, CancellationToken.None);
        await storage.DeleteAsync(new string('b', 64), CancellationToken.None);
        await storage.DeleteAsync("../x", CancellationToken.None);

        Assert.Null(await storage.OpenAsync(hash, CancellationToken.None));
        Assert.Equal(0, count());
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task Two_concurrent_puts_of_the_same_bytes_both_succeed(string kind)
    {
        var (storage, count) = await NewAsync(kind);
        var bytes = RandomNumberGenerator.GetBytes(1 << 20);

        var hashes = await Task.WhenAll(storage.PutAsync(bytes, CancellationToken.None), storage.PutAsync(bytes, CancellationToken.None));

        Assert.Equal(hashes[0], hashes[1]);
        Assert.Equal(1, count());
    }
}
