namespace Nid.Documents.Application;

/// <summary>
/// Content-addressed file store: the key is the lowercase sha256 of the bytes, laid out as <c>{sha[0:2]}/{sha256}</c>
/// with no extension. What reads a document decides who may; the store only maps a hash to its bytes.
/// </summary>
public interface IDocumentStorage
{
    /// <summary>Stores the bytes under their sha256 and returns it; writes nothing when the key already exists.</summary>
    Task<string> PutAsync(ReadOnlyMemory<byte> content, CancellationToken ct);

    /// <summary><c>null</c> for a malformed hash or a missing key. The caller disposes the stream.</summary>
    Task<Stream?> OpenAsync(string sha256, CancellationToken ct);
}

/// <summary>Bound from <c>Documents:Storage</c>: <c>Kind</c> is <c>filesystem</c> or <c>s3</c>.</summary>
public sealed class DocumentStorageOptions
{
    public string Kind { get; set; } = "filesystem";
    public string Path { get; set; } = "/var/lib/nid/documents";
    public string? ServiceUrl { get; set; }
    public string? Bucket { get; set; }
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public string Region { get; set; } = "us-east-1";
    public bool ForcePathStyle { get; set; } = true;
}

/// <summary>Bound from <c>Documents</c>: <c>MaxSizeBytes</c>, <c>McpMaxBytes</c> and the <c>Storage</c> section.</summary>
public sealed class DocumentsOptions
{
    public long MaxSizeBytes { get; set; } = 26214400;

    /// <summary>Design §4.2: the largest document `read_document` sends whole; above it, the metadata and a message.</summary>
    public long McpMaxBytes { get; set; } = 10485760;

    public DocumentStorageOptions Storage { get; set; } = new();
}
