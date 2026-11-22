using System.Security.Cryptography;
using NSign;
using NSign.Providers;
using NSign.Signatures;
using Nid.Ai.Protocol.Signing;

namespace Nid.Ai.Tests;

/// <summary>A worker accepts the Nid key it pinned and, during a Nid key rotation, the previous one: the verifier finds
/// the key by <c>keyid</c>.</summary>
public sealed class KeyRingVerifierTests
{
    private static readonly byte[] Input = "signature base"u8.ToArray();

    [Fact]
    public async Task Each_known_key_verifies_its_own_signature()
    {
        using var current = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var previous = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = WorkerSignatures.Verifier(Ring(current, previous), new NonceCache(TimeProvider.System));

        Assert.Equal(VerificationResult.SuccessfullyVerified, await Verify(verifier, current, "nonce-0000000000000001"));
        Assert.Equal(VerificationResult.SuccessfullyVerified, await Verify(verifier, previous, "nonce-0000000000000002"));
    }

    [Fact]
    public async Task An_unknown_keyid_is_refused()
    {
        using var known = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = WorkerSignatures.Verifier(Ring(known), new NonceCache(TimeProvider.System));

        Assert.NotEqual(VerificationResult.SuccessfullyVerified, await Verify(verifier, stranger, "nonce-0000000000000003"));
    }

    private static Func<string, ECDsa?> Ring(params ECDsa[] keys)
        => id => keys.FirstOrDefault(k => WorkerSignatures.KeyId(k) == id);

    private static async Task<VerificationResult> Verify(IVerifier verifier, ECDsa signer, string nonce)
    {
        var parameters = new SignatureParamsComponent().WithKeyId(WorkerSignatures.KeyId(signer)).WithCreatedNow();
        parameters.Nonce = nonce;
        var signature = await WorkerSignatures.Signer(signer).SignAsync(Input, TestContext.Current.CancellationToken);
        return await verifier.VerifyAsync(parameters, Input, signature, TestContext.Current.CancellationToken);
    }
}
