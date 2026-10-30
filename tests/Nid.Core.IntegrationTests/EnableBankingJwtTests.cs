using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nid.Banking.Infrastructure.EnableBanking;
using NodaTime;

namespace Nid.Core.IntegrationTests;

public class EnableBankingJwtTests
{
    private static byte[] Decode(string base64Url)
        => Convert.FromBase64String(base64Url.Replace('-', '+').Replace('_', '/').PadRight((base64Url.Length + 3) / 4 * 4, '='));

    [Fact]
    public void Header_body_and_signature_follow_the_enable_banking_rules()
    {
        using var rsa = RSA.Create(2048);
        var now = Instant.FromUtc(2026, 10, 29, 9, 0);

        var parts = EnableBankingJwt.Create("app-1234", rsa.ExportPkcs8PrivateKeyPem(), now).Split('.');

        Assert.Equal(3, parts.Length);
        Assert.All(parts, p => Assert.DoesNotMatch("[+/=]", p));
        var header = JsonDocument.Parse(Decode(parts[0])).RootElement;
        Assert.Equal(("JWT", "RS256", "app-1234"), (header.GetProperty("typ").GetString(), header.GetProperty("alg").GetString(), header.GetProperty("kid").GetString()));
        var body = JsonDocument.Parse(Decode(parts[1])).RootElement;
        Assert.Equal(("enablebanking.com", "api.enablebanking.com"), (body.GetProperty("iss").GetString(), body.GetProperty("aud").GetString()));
        Assert.Equal(now.ToUnixTimeSeconds(), body.GetProperty("iat").GetInt64());
        Assert.Equal(now.ToUnixTimeSeconds() + 3600, body.GetProperty("exp").GetInt64());
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void A_key_that_is_not_a_pem_throws_without_echoing_it()
    {
        var e = Assert.ThrowsAny<Exception>(() => EnableBankingJwt.Create("app", "not-a-key-SECRET", Instant.FromUtc(2026, 10, 29, 9, 0)));
        Assert.DoesNotContain("SECRET", e.Message);
    }
}
