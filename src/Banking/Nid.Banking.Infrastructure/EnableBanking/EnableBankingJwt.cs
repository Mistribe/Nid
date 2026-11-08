using System.Security.Cryptography;
using System.Text;
using NodaTime;

namespace Nid.Banking.Infrastructure.EnableBanking;

/// <summary>The RS256 token Enable Banking asks on every call: signed with the application's private key, valid one hour at most.</summary>
public static class EnableBankingJwt
{
    private const int TtlSeconds = 3600;

    public static string Create(string appId, string privateKeyPem, Instant now)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var iat = now.ToUnixTimeSeconds();
        var header = $$"""{"typ":"JWT","alg":"RS256","kid":"{{Escape(appId)}}"}""";
        var body = $$"""{"iss":"enablebanking.com","aud":"api.enablebanking.com","iat":{{iat}},"exp":{{iat + TtlSeconds}}}""";
        var signingInput = Base64Url(Encoding.UTF8.GetBytes(header)) + "." + Base64Url(Encoding.UTF8.GetBytes(body));
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    private static string Escape(string value) => System.Text.Json.JsonEncodedText.Encode(value).ToString();

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
