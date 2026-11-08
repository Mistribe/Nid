using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Nid.Documents.Application;
using Nid.Platform.Contracts;

namespace Nid.Documents.Infrastructure.Storage;

/// <summary>
/// The same <c>{sha[0:2]}/{sha256}</c> keys in an S3 bucket (MinIO, S3, any compatible store). The bucket must exist.
/// Two writers of the same bytes may both pass the existence check: the second <c>PutObject</c> overwrites an identical object.
/// </summary>
public sealed class S3DocumentStorage : IDocumentStorage, IDisposable
{
    private readonly AmazonS3Client client;
    private readonly string bucket;

    public S3DocumentStorage(DocumentStorageOptions options)
    {
        bucket = options.Bucket ?? throw new InvalidOperationException("Documents:Storage:Bucket is required for the s3 storage.");
        var config = new AmazonS3Config { ForcePathStyle = options.ForcePathStyle };
        if (options.ServiceUrl is { Length: > 0 } url)
            config.ServiceURL = url;
        else
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region);
        client = options.AccessKey is { Length: > 0 } key
            ? new AmazonS3Client(new BasicAWSCredentials(key, options.SecretKey), config)
            : new AmazonS3Client(config);
    }

    public void Dispose() => client.Dispose();

    public async Task<string> PutAsync(ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(content.Span));
        var key = KeyOf(hash);
        if (await ExistsAsync(key, ct))
            return hash;

        using var body = new MemoryStream(content.ToArray(), writable: false);
        await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = body }, ct);
        return hash;
    }

    public async Task<Stream?> OpenAsync(string sha256, CancellationToken ct)
    {
        if (!AssetHash.IsValid(sha256))
            return null;

        try
        {
            var response = await client.GetObjectAsync(bucket, KeyOf(sha256), ct);
            return response.ResponseStream; // disposing the stream releases the response
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound || e.ErrorCode == "NoSuchKey")
        {
            return null;
        }
    }

    private async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        try
        {
            await client.GetObjectMetadataAsync(bucket, key, ct);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static string KeyOf(string hash) => $"{hash[..2]}/{hash}";
}
