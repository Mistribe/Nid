using System.Security.Cryptography;
using System.Text;

namespace Nid.Ai.Protocol.Sealing;

/// <summary>A request sealed by Nid to the worker's pinned key: <c>enc || ciphertext</c>, base mode
/// (<c>docs/ai/README.md</c> §4.1). Both sides keep the <see cref="ResponseSecret"/> the response is bound to.</summary>
public static class SealedRequest
{
    private static readonly byte[] Info = Encoding.ASCII.GetBytes("nid-worker request v1");
    private static readonly byte[] ResponseContext = Encoding.ASCII.GetBytes("nid-worker response v1");

    public static (byte[] Message, ResponseSecret Secret) Seal(ReadOnlySpan<byte> workerPublicKey, ReadOnlySpan<byte> plaintext)
    {
        var (enc, ciphertext, exported) =
            HpkeSuite256.Seal(workerPublicKey, plaintext, Info, ResponseContext, ResponseSecret.ExportedLength);
        return ([.. enc, .. ciphertext], new ResponseSecret(exported, enc));
    }

    /// <summary>Opens a request on the worker. Any failure is the same <c>sealed_invalid</c>: the caller learns nothing
    /// about which part was wrong.</summary>
    public static (byte[] Plaintext, ResponseSecret Secret) Open(WorkerKeyPair key, ReadOnlySpan<byte> message)
    {
        if (message.Length <= HpkeSuite256.EncLength)
            throw new ProtocolException("sealed_invalid");

        var enc = message[..HpkeSuite256.EncLength];
        try
        {
            var (plaintext, exported) = HpkeSuite256.Open(key.PrivateKey, enc, message[HpkeSuite256.EncLength..], Info,
                ResponseContext, ResponseSecret.ExportedLength);
            return (plaintext, new ResponseSecret(exported, enc.ToArray()));
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            throw new ProtocolException("sealed_invalid");
        }
    }
}
