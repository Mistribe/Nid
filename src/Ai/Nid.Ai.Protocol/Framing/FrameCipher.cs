using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Nid.Ai.Protocol.Sealing;

namespace Nid.Ai.Protocol.Framing;

/// <summary>The response key schedule of RFC 9458 §4.4, AES-128-GCM: <c>salt = enc || response_nonce</c>,
/// <c>prk = Extract(salt, exported)</c>, <c>key = Expand(prk, "key", 16)</c>, <c>nonce = Expand(prk, "nonce", 12)</c>.
/// Frame <c>i</c> is sealed with <c>nonce XOR i</c>, so a frame dropped, repeated or moved does not open.</summary>
internal sealed class FrameCipher : IDisposable
{
    public const int ResponseNonceLength = 16;
    public const int TagLength = 16;

    private static readonly byte[] KeyLabel = Encoding.ASCII.GetBytes("key");
    private static readonly byte[] NonceLabel = Encoding.ASCII.GetBytes("nonce");

    private readonly AesGcm _aead;
    private readonly byte[] _baseNonce;

    public FrameCipher(ResponseSecret secret, ReadOnlySpan<byte> responseNonce)
    {
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, secret.Exported, [.. secret.Enc, .. responseNonce]);
        _aead = new AesGcm(HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, KeyLabel), TagLength);
        _baseNonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, NonceLabel);
        CryptographicOperations.ZeroMemory(prk);
    }

    public byte[] Seal(long counter, FrameType type, ReadOnlySpan<byte> content)
    {
        byte[] plaintext = [(byte)type, .. content];
        var sealedFrame = new byte[plaintext.Length + TagLength];
        _aead.Encrypt(Nonce(counter), plaintext, sealedFrame.AsSpan(0, plaintext.Length), sealedFrame.AsSpan(plaintext.Length));
        return sealedFrame;
    }

    /// <summary>The frame's plaintext (type byte first); <see cref="AuthenticationTagMismatchException"/> when it is not
    /// frame <paramref name="counter"/> of this response.</summary>
    public byte[] Open(long counter, ReadOnlySpan<byte> sealedFrame)
    {
        var plaintext = new byte[sealedFrame.Length - TagLength];
        _aead.Decrypt(Nonce(counter), sealedFrame[..plaintext.Length], sealedFrame[plaintext.Length..], plaintext);
        return plaintext;
    }

    public void Dispose() => _aead.Dispose();

    private byte[] Nonce(long counter)
    {
        var nonce = (byte[])_baseNonce.Clone();
        var tail = nonce.AsSpan(4);
        BinaryPrimitives.WriteInt64BigEndian(tail, BinaryPrimitives.ReadInt64BigEndian(tail) ^ counter);
        return nonce;
    }
}
