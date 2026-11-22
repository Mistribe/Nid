using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NSign;
using NSign.Providers;

namespace Nid.Ai.Tests;

/// <summary>RFC 9421 §B.2.4: NSign verifies the RFC's own ECDSA P-256 example before we trust it with ours.</summary>
public sealed class SignatureVectorTests
{
    [Fact]
    public async Task Rfc9421_B24_verifies()
    {
        using var response = await Fetch(tamper: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Tampered_B24_fails()
        => await Assert.ThrowsAnyAsync<Exception>(() => Fetch(tamper: true));

    private static async Task<HttpResponseMessage> Fetch(bool tamper)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(Vectors.Copied("rfc9421", "test-key-ecc-p256.pem")));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IVerifier>(new ECDsaP256Sha256SignatureProvider(null, key, "test-key-ecc-p256"));
        services.Configure<SignatureVerificationOptions>(o =>
        {
            o.ShouldVerify = signature => signature.Name == "sig-b24";
            o.ExpiresRequired = false;
            o.MaxSignatureAge = null;
        });
        services.AddHttpClient("b24")
            .AddSignatureVerificationHandler()
            .ConfigurePrimaryHttpMessageHandler(() => new RfcResponse(tamper));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("b24");
        return await client.GetAsync("https://example.com/foo", TestContext.Current.CancellationToken);
    }

    /// <summary>Answers every request with <c>b24-response.txt</c>; <c>tamper</c> changes one covered field.</summary>
    private sealed class RfcResponse(bool tamper) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = File.ReadAllText(Vectors.Copied("rfc9421", "b24-response.txt"));
            var (head, body) = (text[..text.IndexOf("\n\n", StringComparison.Ordinal)], text[(text.IndexOf("\n\n", StringComparison.Ordinal) + 2)..]);

            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
            foreach (var line in head.Split('\n').Skip(1))
            {
                var (name, value) = (line[..line.IndexOf(':')], line[(line.IndexOf(':') + 1)..].Trim());
                switch (name)
                {
                    case "Content-Type":
                        content.Headers.ContentType = MediaTypeHeaderValue.Parse(tamper ? "text/plain" : value);
                        break;
                    case "Content-Length":
                        content.Headers.ContentLength = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "Content-Digest":
                        content.Headers.TryAddWithoutValidation(name, value);
                        break;
                    default:
                        response.Headers.TryAddWithoutValidation(name, value);
                        break;
                }
            }

            return Task.FromResult(response);
        }
    }
}
