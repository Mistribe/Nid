using System.Security.Cryptography;

namespace Nid.Ai.Protocol.Sealing;

// .NET 11's HPKE is experimental (SYSLIB5009): this is the only file that touches it, so a change of its shape lands here.
// Every other file sees bytes in, bytes out. The RFC 9180 vectors replay through Replay() on every build.
#pragma warning disable SYSLIB5009

/// <summary>The protocol's one HPKE suite: DHKEM(P-256, HKDF-SHA256), HKDF-SHA256, AES-128-GCM, base mode
/// (<c>docs/ai/README.md</c> §4.1).</summary>
internal static class HpkeSuite256
{
    private static readonly HpkeSuite Suite = new(HpkeKem.DHKEM_P256_HKDF_SHA256, HpkeKdf.HKDF_SHA256, HpkeAead.AES_128_GCM);

    /// <summary>The encapsulated secret's length: an uncompressed P-256 point.</summary>
    public const int EncLength = 65;

    public static bool IsSupported => Hpke.IsSupported(Suite);

    public static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        using var key = Hpke.GenerateKey(Suite);
        return (key.ExportDecapsulationKey(), key.ExportEncapsulationKey());
    }

    public static byte[] PublicKeyOf(ReadOnlySpan<byte> privateKey)
    {
        using var key = Hpke.ImportDecapsulationKey(Suite, privateKey);
        return key.ExportEncapsulationKey();
    }

    /// <summary>Seals one message to <paramref name="publicKey"/> and exports a secret of the same context.</summary>
    public static (byte[] Enc, byte[] Ciphertext, byte[] Exported) Seal(
        ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> info, ReadOnlySpan<byte> exportContext,
        int exportLength)
    {
        using var key = Hpke.ImportEncapsulationKey(Suite, publicKey);
        using var sender = key.CreateSender(out var enc, info);
        var ciphertext = sender.Seal(plaintext, []);
        return (enc, ciphertext, sender.Export(exportContext, exportLength));
    }

    /// <summary>Opens one message sealed to <paramref name="privateKey"/> and exports the same secret as the sender.</summary>
    public static (byte[] Plaintext, byte[] Exported) Open(
        ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> enc, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> info,
        ReadOnlySpan<byte> exportContext, int exportLength)
    {
        using var key = Hpke.ImportDecapsulationKey(Suite, privateKey);
        using var recipient = key.CreateRecipient(enc, info);
        var plaintext = recipient.Open(ciphertext, []);
        return (plaintext, recipient.Export(exportContext, exportLength));
    }

    /// <summary>Whether .NET defines this P-256 suite (export-only AEADs it does not).</summary>
    internal static bool Defines(int kdf, int aead)
        => Enum.IsDefined((HpkeKdf)kdf) && Enum.IsDefined((HpkeAead)aead);

    /// <summary>Replays one RFC 9180 test vector (DHKEM(P-256), any KDF and AEAD): the recipient key derived from
    /// <paramref name="ikm"/>, every message opened in order on one context, every export.</summary>
    internal static (byte[] PublicKey, byte[][] Plaintexts, byte[][] Exports) Replay(
        int kdf, int aead, byte[] ikm, byte[] enc, byte[] info, (byte[] Ciphertext, byte[] Aad)[] messages,
        (byte[] Context, int Length)[] exports)
    {
        var suite = new HpkeSuite(HpkeKem.DHKEM_P256_HKDF_SHA256, (HpkeKdf)kdf, (HpkeAead)aead);
        using var key = Hpke.DeriveKey(suite, ikm);
        using var recipient = key.CreateRecipient(enc, info);
        return (key.ExportEncapsulationKey(),
            [.. messages.Select(m => recipient.Open(m.Ciphertext, m.Aad))],
            [.. exports.Select(x => recipient.Export(x.Context, x.Length))]);
    }
}
