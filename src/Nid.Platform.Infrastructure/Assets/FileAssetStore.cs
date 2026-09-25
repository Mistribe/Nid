using System.Security.Cryptography;
using Nid.Platform.Contracts;

namespace Nid.Platform.Infrastructure.Assets;

/// <summary>
/// 1C2c design §3: <c>{root}/{first 2 hex chars}/{sha256 hex}.webp</c>. A write goes to a temporary file in the same
/// folder, flushed to disk, then renamed — atomic on one file system — so a reader never sees half a file; a file
/// already there is the same content and is left alone. A read validates the hash before any file-system call.
/// </summary>
public sealed class FileAssetStore(AssetOptions options) : IAssetStore
{
    public const string WebpContentType = "image/webp";

    private const string WebpExtension = ".webp";

    private readonly string root = Path.GetFullPath(options.Root);

    public async Task<string> PutAsync(ReadOnlyMemory<byte> content, string contentType, CancellationToken ct)
    {
        if (contentType != WebpContentType)
            throw new ArgumentOutOfRangeException(nameof(contentType), contentType, "The asset store holds WebP images only (1C2c).");

        var hash = Convert.ToHexStringLower(SHA256.HashData(content.Span));
        var final = PathOf(hash);
        if (File.Exists(final))
            return hash; // same name, same content: nothing to write

        var folder = Path.GetDirectoryName(final)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".{hash}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await file.WriteAsync(content, ct);
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporary, final, overwrite: false);
            }
            catch (IOException) when (File.Exists(final))
            {
                // Another writer of the same content renamed first (Review Focus 5): its file is this one.
            }
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        return hash;
    }

    public Task<Stream?> OpenAsync(string hash, CancellationToken ct)
    {
        if (!AssetHash.IsValid(hash))
            return Task.FromResult<Stream?>(null);

        try
        {
            return Task.FromResult<Stream?>(
                new FileStream(PathOf(hash), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    private string PathOf(string hash) => Path.Combine(root, hash[..2], hash + WebpExtension);
}
