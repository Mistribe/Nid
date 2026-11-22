using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nid.Ai.Worker;

/// <summary>The worker's TLS certificate: self-signed, ECDSA P-256, ten years. Nid pins its SHA-256 at pairing when it
/// reaches the worker directly; behind a proxy, the proxy's public certificate is what Nid sees.</summary>
public static class SelfSignedCertificate
{
    public static byte[] Create(IReadOnlyList<string> hostnames, DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={hostnames[0]}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        foreach (var hostname in hostnames)
            names.AddDnsName(hostname);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(10));
        return certificate.Export(X509ContentType.Pkcs12);
    }

    public static X509Certificate2 Load(string base64) => X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(base64), null);

    public static string Sha256(X509Certificate2 certificate) => Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
}
