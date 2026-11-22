using System.Security.Cryptography;

namespace Nid.Ai.Protocol.Keys;

/// <summary>What an admin compares at pairing, and the <c>keyid</c> of a signature: SHA-256 of a public key.</summary>
public static class KeyFingerprint
{
    /// <summary>Lowercase hex in 16 groups of four, for a person to read off a console.</summary>
    public static string Of(ReadOnlySpan<byte> publicKey)
        => string.Join(' ', Compact(publicKey).Chunk(4).Select(group => new string(group)));

    public static string Compact(ReadOnlySpan<byte> publicKey) => Convert.ToHexStringLower(SHA256.HashData(publicKey));
}
