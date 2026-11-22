using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSign;
using NSign.AspNetCore;
using NSign.Client;
using NSign.Signatures;
using Nid.Ai.Protocol.Signing;

namespace Nid.Ai.Tests;

/// <summary>A worker-shaped server (digest, then signature, then the endpoint) and a Nid-shaped client (digest, then
/// signature), both configured by <see cref="WorkerSignatures"/>.</summary>
public sealed class SignaturePipelineTests : IAsyncLifetime
{
    private readonly ECDsa _nidKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private WebApplication _worker = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddContentDigestVerification();
        builder.Services.AddSignatureVerification(WorkerSignatures.Verifier(_nidKey, new NonceCache(TimeProvider.System)));
        builder.Services.Configure<RequestSignatureVerificationOptions>(o =>
            WorkerSignatures.ConfigureVerification(o, TimeProvider.System));

        _worker = builder.Build();
        _worker.UseContentDigestVerification();
        _worker.UseSignatureVerification();
        _worker.MapPost("/v1/echo", () => "ok");
        await _worker.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _worker.DisposeAsync();
        _nidKey.Dispose();
    }

    [Fact]
    public async Task A_signed_request_passes()
        => Assert.Equal(HttpStatusCode.OK, await Send(Client(_nidKey)));

    [Fact]
    public async Task An_unsigned_request_is_refused()
    {
        using var client = _worker.GetTestClient();
        using var response = await client.PostAsync("/v1/echo", Body(), TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_body_that_does_not_match_its_digest_is_refused()
        => Assert.Equal(HttpStatusCode.BadRequest, await Send(Client(_nidKey, last: new SwapBody())));

    [Theory]
    [InlineData(-65)]
    [InlineData(65)] // past 60 s by more than the whole second `created` is rounded down to
    public async Task A_signature_outside_the_window_is_refused(int seconds)
        => AssertRefused(await Send(Client(_nidKey, p => p.WithCreated(DateTimeOffset.UtcNow.AddSeconds(seconds)))));

    [Theory]
    [InlineData(-30)]
    [InlineData(30)]
    public async Task A_signature_inside_the_window_passes(int seconds)
        => Assert.Equal(HttpStatusCode.OK, await Send(Client(_nidKey, p => p.WithCreated(DateTimeOffset.UtcNow.AddSeconds(seconds)))));

    [Fact]
    public async Task A_replayed_request_is_refused()
    {
        // The recorder keeps the signed request without sending it: its first copy passes, so the copy is faithful
        // and the second is refused for its nonce alone.
        var recorder = new Recorder();
        await Send(Client(_nidKey, last: recorder));

        Assert.Equal(HttpStatusCode.OK, await Replay(recorder));
        AssertRefused(await Replay(recorder));
    }

    [Fact]
    public async Task Another_key_is_refused()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        AssertRefused(await Send(Client(other)));
    }

    [Fact]
    public async Task The_right_key_under_another_keyid_is_refused()
        => AssertRefused(await Send(Client(_nidKey, p => p.WithKeyId(new string('0', 64)), signerSetsParams: false)));

    [Fact]
    public async Task A_forged_request_does_not_use_up_its_nonce()
    {
        // A request whose signature fails must leave no trace: the nonce cache only remembers verified requests.
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var nonce = "forged-nonce-0123456789";

        AssertRefused(await Send(Client(other, p => p.Nonce = nonce)));
        Assert.Equal(HttpStatusCode.OK, await Send(Client(_nidKey, p => p.Nonce = nonce)));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("a nonce far longer than any signer of this protocol would ever send, padded out to 80 chars")]
    public async Task A_nonce_of_the_wrong_shape_is_refused(string nonce)
        => AssertRefused(await Send(Client(_nidKey, p => p.Nonce = nonce)));

    /// <summary>NSign answers 401 when a signature does not verify and 400 when its parameters are refused (nonce,
    /// <c>created</c> ahead): both are a refusal, and the endpoint never ran.</summary>
    private static void AssertRefused(HttpStatusCode status)
        => Assert.Contains(status, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized });

    private async Task<HttpStatusCode> Replay(Recorder recorder)
    {
        using var client = _worker.GetTestClient();
        using var replay = new HttpRequestMessage(HttpMethod.Post, recorder.Uri) { Content = new ByteArrayContent(recorder.Body) };
        foreach (var (name, values) in recorder.Headers)
            replay.Headers.TryAddWithoutValidation(name, values);
        foreach (var (name, values) in recorder.ContentHeaders)
            replay.Content.Headers.TryAddWithoutValidation(name, values);
        using var response = await client.SendAsync(replay, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private HttpClient Client(
        ECDsa key, Action<SignatureParamsComponent>? tweak = null, DelegatingHandler? last = null, bool signerSetsParams = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISigner>(WorkerSignatures.Signer(key));
        services.Configure<AddContentDigestOptions>(o => o.WithHash(AddContentDigestOptions.Hash.Sha256));
        services.ConfigureMessageSigningOptions(o =>
        {
            WorkerSignatures.ConfigureSigning(o);
            o.UseUpdateSignatureParams = signerSetsParams;
            if (tweak is not null)
            {
                var set = o.SetParameters!;
                o.SetParameters = p =>
                {
                    set(p);
                    tweak(p);
                };
            }
        });
        var client = services.AddHttpClient("worker").AddContentDigestAndSigningHandlers();
        if (last is not null)
            client.AddHttpMessageHandler(() => last);
        client.ConfigurePrimaryHttpMessageHandler(() => _worker.GetTestServer().CreateHandler());

        var http = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient("worker");
        http.BaseAddress = new Uri("http://worker.test");
        return http;
    }

    private static async Task<HttpStatusCode> Send(HttpClient client)
    {
        using (client)
        using (var response = await client.PostAsync("/v1/echo", Body(), TestContext.Current.CancellationToken))
            return response.StatusCode;
    }

    private static ByteArrayContent Body() => new("sealed bytes"u8.ToArray());

    /// <summary>Replaces the body after it was digested and signed, keeping every header.</summary>
    private sealed class SwapBody : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var swapped = new ByteArrayContent("other bytes!"u8.ToArray());
            foreach (var (name, values) in request.Content!.Headers)
                swapped.Headers.TryAddWithoutValidation(name, values);
            request.Content = swapped;
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Keeps a copy of the signed request as it leaves the client, and answers 204 without sending it.</summary>
    private sealed class Recorder : DelegatingHandler
    {
        public Uri Uri { get; private set; } = null!;
        public byte[] Body { get; private set; } = [];
        public List<(string, IEnumerable<string>)> Headers { get; } = [];
        public List<(string, IEnumerable<string>)> ContentHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri!;
            Body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            Headers.AddRange(request.Headers.Select(h => (h.Key, h.Value)));
            ContentHeaders.AddRange(request.Content.Headers.Select(h => (h.Key, h.Value)));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
