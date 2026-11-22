using System.Security.Cryptography;

namespace Nid.Ai.Protocol.Sealing;

/// <summary>A worker's HPKE key pair: Nid seals every request to <see cref="PublicKey"/>, which Nid pins at pairing.</summary>
public sealed class WorkerKeyPair : IDisposable
{
    private readonly byte[] _privateKey;

    private WorkerKeyPair(byte[] privateKey, byte[] publicKey)
    {
        _privateKey = privateKey;
        PublicKey = publicKey;
    }

    /// <summary>The 65-byte uncompressed P-256 point.</summary>
    public byte[] PublicKey { get; }

    internal ReadOnlySpan<byte> PrivateKey => _privateKey;

    public static WorkerKeyPair Generate()
    {
        var (privateKey, publicKey) = HpkeSuite256.GenerateKeyPair();
        return new WorkerKeyPair(privateKey, publicKey);
    }

    public static WorkerKeyPair Import(ReadOnlySpan<byte> privateKey)
    {
        try
        {
            return new WorkerKeyPair(privateKey.ToArray(), HpkeSuite256.PublicKeyOf(privateKey));
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            throw new ProtocolException("sealed_invalid");
        }
    }

    public byte[] ExportPrivateKey() => (byte[])_privateKey.Clone();

    public void Dispose() => CryptographicOperations.ZeroMemory(_privateKey);
}
