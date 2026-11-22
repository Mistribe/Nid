namespace Nid.Ai.Protocol.Sealing;

/// <summary>What a response is bound to: the secret exported from its request's HPKE context and that request's
/// encapsulation (RFC 9458 §4.4). Nid keeps it, encrypted, until a job's result is fetched.</summary>
public sealed record ResponseSecret
{
    /// <summary>The exported secret's length: the AEAD key length of AES-128-GCM.</summary>
    public const int ExportedLength = 16;

    internal ResponseSecret(byte[] exported, byte[] enc)
    {
        Exported = exported;
        Enc = enc;
    }

    public byte[] Exported { get; }

    public byte[] Enc { get; }

    /// <summary><c>Exported(16) || Enc(65)</c>.</summary>
    public byte[] ToBytes() => [.. Exported, .. Enc];

    public static ResponseSecret FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ExportedLength + HpkeSuite256.EncLength)
            throw new ProtocolException("sealed_invalid");

        return new ResponseSecret(bytes[..ExportedLength].ToArray(), bytes[ExportedLength..].ToArray());
    }
}
