using System.Security.Cryptography;
using Nid.Banking.Contracts;

namespace Nid.Banking.Tests;

/// <summary>#382: what « Mon application » accepts as an adult's Enable Banking key.</summary>
public sealed class BankingKeyTests
{
    private static string RsaPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    [Fact]
    public void Only_an_rsa_private_key_is_one()
    {
        using var rsa = RSA.Create(2048);
        var cert = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=nid", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)).ExportCertificatePem();
        Assert.Equal(
            [true, false, false, false],
            [BankingKey.IsRsaPrivateKey(rsa.ExportPkcs8PrivateKeyPem()), BankingKey.IsRsaPrivateKey(rsa.ExportRSAPublicKeyPem()), BankingKey.IsRsaPrivateKey(""), BankingKey.IsRsaPrivateKey(cert)]);
    }

    [Fact]
    public void An_application_prints_its_id_and_never_its_key()
    {
        var text = new EnableBankingApp("app-alice", RsaPem()).ToString();
        Assert.Contains("app-alice", text);
        Assert.DoesNotContain("PRIVATE KEY", text);
    }
}
