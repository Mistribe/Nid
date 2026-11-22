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

    /// <summary>The worker's verifier for the Nid key it pinned.</summary>
    public static ECDsaP256Sha256SignatureProvider Verifier(ECDsa publicKey)
        => new(null, publicKey, KeyId(publicKey));

    public static string KeyId(ECDsa key) => KeyFingerprint.Compact(key.ExportSubjectPublicKeyInfo());

    public static void ConfigureSigning(MessageSigningOptions options)
    {
        options.SignatureName = "nid";
        foreach (var component in Components)
            options.WithMandatoryComponent(component);
        options.SetParameters = parameters =>
        {
            parameters.WithCreatedNow();
            parameters.Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            parameters.Tag = Tag;
        };
    }

    public static void ConfigureVerification(SignatureVerificationOptions options, NonceCache nonces, TimeProvider time)
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
            && parameters.Nonce is { Length: > 0 } nonce
            && nonces.TryUse(nonce);
    }
}
