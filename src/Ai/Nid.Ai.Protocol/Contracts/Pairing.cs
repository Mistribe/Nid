using System.Security.Cryptography;

namespace Nid.Ai.Protocol.Contracts;

/// <summary>What Nid sends to pair (<c>POST /v1/pair</c>), sealed to the worker's key and signed by
/// <see cref="NidPublicKey"/> itself: the code proves an admin read it off the worker's console, the signature that Nid
/// holds the key the worker is about to pin.</summary>
public sealed record PairRequest(string NidPublicKey, string Code) : IContract
{
    /// <summary>No 0/O, 1/I/L: a person reads it off a console.</summary>
    public const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public const int CodeLength = 8;

    public bool IsValid()
        => Code.Length == CodeLength && Code.All(CodeAlphabet.Contains) && Keys.IsP256SubjectPublicKey(NidPublicKey);
}

/// <summary>The worker's answer to a pairing: the SHA-256 of its TLS certificate (hex, lowercase) for Nid to pin when it
/// reaches the worker directly, and its first declaration.</summary>
public sealed record PairResponse(string TlsCertificateSha256, WorkerDeclaration Declaration) : IContract
{
    public bool IsValid()
        => TlsCertificateSha256.Length == 64 && TlsCertificateSha256.All(char.IsAsciiHexDigitLower) && Declaration.IsValid();
}

/// <summary>The worker's new HPKE public key (65 bytes, base64), sent in a response bound to a request sealed to its old
/// key: only the holder of the old key can announce the new one.</summary>
public sealed record RotateWorkerKeyResponse(string NewPublicKey) : IContract
{
    public bool IsValid() => Keys.IsBase64OfLength(NewPublicKey, 65);
}

/// <summary>Nid's new signing key (SPKI, base64), signed by the old one. The worker keeps it pending until its operator
/// confirms the fingerprint on the console.</summary>
public sealed record RotateNidKeyRequest(string NewNidPublicKey) : IContract
{
    public bool IsValid() => Keys.IsP256SubjectPublicKey(NewNidPublicKey);
}

internal static class Keys
{
    public static bool IsBase64OfLength(string value, int length)
    {
        var bytes = new byte[length + 1];
        return Convert.TryFromBase64String(value, bytes, out var written) && written == length;
    }

    public static bool IsP256SubjectPublicKey(string value)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(value), out _);
            return key.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value;
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
