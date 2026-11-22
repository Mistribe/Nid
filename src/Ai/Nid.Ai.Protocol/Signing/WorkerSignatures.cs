using System.Buffers.Text;
using System.Security.Cryptography;
using NSign;
using NSign.Providers;
using NSign.Signatures;
using Nid.Ai.Protocol.Keys;

namespace Nid.Ai.Protocol.Signing;

/// <summary>How Nid signs its requests to a worker and how the worker checks them (RFC 9421, <c>docs/ai/README.md</c>
/// §4.1): ECDSA P-256 over <c>@method</c>, <c>@target-uri</c> and <c>content-digest</c> (which covers the sealed body and
/// its encapsulation), with <c>created</c>, <c>nonce</c>, <c>keyid</c> and the tag <see cref="Tag"/>. The worker checks the
/// digest and the signature before it opens the body.</summary>
public static class WorkerSignatures
{
    public const string Tag = "nid-worker";

    /// <summary>How far <c>created</c> may be from the verifier's clock, either way.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private static readonly SignatureComponent[] Components =
        [SignatureComponent.Method, SignatureComponent.RequestTargetUri, SignatureComponent.ContentDigest];

    /// <summary>Nid's signer; its <c>keyid</c> is the compact fingerprint of its public key.</summary>
    public static ECDsaP256Sha256SignatureProvider Signer(ECDsa privateKey)
        => new(privateKey, privateKey, KeyId(privateKey));

    /// <summary>The worker's verifier for the Nid key it pinned. A nonce is used up only once its signature verified,
    /// so a forged request leaves nothing in <paramref name="nonces"/>.</summary>
    public static IVerifier Verifier(ECDsa publicKey, NonceCache nonces)
        => new NonceConsumingVerifier(new ECDsaP256Sha256SignatureProvider(null, publicKey, KeyId(publicKey)), nonces);

    public static string KeyId(ECDsa key) => KeyFingerprint.Compact(key.ExportSubjectPublicKeyInfo());

    public static void ConfigureSigning(MessageSigningOptions options)
    {
        options.SignatureName = "nid";
        foreach (var component in Components)
            options.WithMandatoryComponent(component);
        options.SetParameters = parameters =>
        {
            parameters.WithCreatedNow();
            parameters.Nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
            parameters.Tag = Tag;
        };
    }

    /// <summary>Checks before the signature: components, tag, <c>created</c> no further than <see cref="Window"/> either
    /// way, a nonce of the shape this protocol sends. Whether the nonce was seen is checked after it, by
    /// <see cref="Verifier"/>.</summary>
    public static void ConfigureVerification(SignatureVerificationOptions options, TimeProvider time)
    {
        options.TagsToVerify.Add(Tag);
        foreach (var component in Components)
            options.RequiredSignatureComponents.Add(component);
        options.TagRequired = true;
        options.CreatedRequired = true;
        options.ExpiresRequired = false;
        options.NonceRequired = true;
        options.KeyIdRequired = true;
        options.MaxSignatureAge = Window;
        options.VerifyNonce = parameters =>
            parameters.Created is { } created
            && created <= time.GetUtcNow() + Window
            && parameters.Nonce is { Length: >= MinNonceLength and <= MaxNonceLength };
    }

    // 16 random bytes in base64url are 22 characters; the bounds keep a forged request from storing much.
    private const int MinNonceLength = 16;
    private const int MaxNonceLength = 64;

    private sealed class NonceConsumingVerifier(IVerifier inner, NonceCache nonces) : IVerifier
    {
        public async Task<VerificationResult> VerifyAsync(
            SignatureParamsComponent signatureParams, ReadOnlyMemory<byte> input, ReadOnlyMemory<byte> expectedSignature,
            CancellationToken cancellationToken)
        {
            var result = await inner.VerifyAsync(signatureParams, input, expectedSignature, cancellationToken);
            return result == VerificationResult.SuccessfullyVerified && !nonces.TryUse(signatureParams.Nonce!)
                ? VerificationResult.SignatureMismatch
                : result;
        }
    }
}
