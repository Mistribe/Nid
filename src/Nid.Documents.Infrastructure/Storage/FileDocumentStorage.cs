using System.Security.Cryptography;
using Nid.Documents.Application;
using Nid.Platform.Contracts;

namespace Nid.Documents.Infrastructure.Storage;

/// <summary>
/// <c>{root}/{sha[0:2]}/{sha256}</c>, like the asset store but with no extension and no content-type check. A write goes
/// to a temporary file in the same folder, flushed, then renamed; a file already there is the same content.
/// </summary>
public sealed class FileDocumentStorage(DocumentStorageOptions options) : IDocumentStorage
{
    private readonly string root = Path.GetFullPath(options.Path);

    public async Task<string> PutAsync(ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(content.Span));
        var final = PathOf(hash);
        if (File.Exists(final))
            return hash;

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
                // A concurrent writer of the same bytes renamed first: its file is this one.
            }
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        return hash;
    }

    public Task<Stream?> OpenAsync(string sha256, CancellationToken ct)
    {
        if (!AssetHash.IsValid(sha256))
            return Task.FromResult<Stream?>(null);

        try
        {
            return Task.FromResult<Stream?>(
                new FileStream(PathOf(sha256), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    private string PathOf(string hash) => Path.Combine(root, hash[..2], hash);
}
