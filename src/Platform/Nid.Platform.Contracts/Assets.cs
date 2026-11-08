using System.Diagnostics.CodeAnalysis;

namespace Nid.Platform.Contracts;

/// <summary>Where the asset store keeps its files (1C2c design §3), bound from the <c>Assets</c> section by the host.</summary>
public sealed class AssetOptions
{
    /// <summary>
    /// A mounted volume in production (its place in the reference compose and in the backup is 1F's). The Aspire
    /// AppHost points development at the git-ignored <c>.data/assets</c> of the repository.
    /// </summary>
    public string Root { get; set; } = "/var/lib/nid/assets";
}

/// <summary>The name of an asset: the lowercase hexadecimal SHA-256 of its bytes, never a name from outside (design §3).</summary>
public static class AssetHash
{
    public const int Length = 64;

    /// <summary><c>^[0-9a-f]{64}$</c>, written without a regular expression: checked before any file-system call.</summary>
    public static bool IsValid([NotNullWhen(true)] string? hash)
        => hash is { Length: Length } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// Content-addressed assets (1C2c design §3): catalog icons now, stage 4 documents later. What reads an asset decides
/// who may: the store only guarantees that a name is a hash and a hash names its content. No delete in 1C2c.
/// </summary>
public interface IAssetStore
{
    /// <summary>
    /// Stores <paramref name="content"/> once and answers its hash: the same content written twice writes nothing the
    /// second time. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the volume
    /// refuses the write; the caller decides what that means.
    /// </summary>
    Task<string> PutAsync(ReadOnlyMemory<byte> content, string contentType, CancellationToken ct);

    /// <summary>The asset named <paramref name="hash"/>, or <c>null</c> when the hash is malformed or nothing is stored under it.</summary>
    Task<Stream?> OpenAsync(string hash, CancellationToken ct);
}
