using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Infrastructure.Cloud;
using NodaTime;

namespace Nid.Ai.Tests;

/// <summary>#556: each provider's embeddings call in its own shape, answered by a fake provider: the vectors in the texts'
/// order, the input tokens, and its failures worded as every adapter words them.</summary>
public sealed class CloudEmbedAdapterTests
{
    private static readonly IReadOnlyList<string> Texts = ["Facture", "Contrat"];

    [Theory]
    [InlineData("openai", "api.openai.com", "text-embedding-3-small")]
    [InlineData("mistral", "api.mistral.ai", "mistral-embed")]
    public async Task A_chat_completions_provider_embeds_a_batch_by_index(string provider, string host, string model)
    {
        // Answered out of order: the index puts each vector back in its place.
        var fake = new FakeProvider(HttpStatusCode.OK, """
            {"object":"list","model":"m","data":[{"object":"embedding","index":1,"embedding":[0.3,0.4]},{"object":"embedding","index":0,"embedding":[0.1,0.2]}],
             "usage":{"prompt_tokens":5,"total_tokens":5}}
            """);
        ICloudAdapter adapter = provider == "openai" ? new OpenAiAdapter(Http(fake, host)) : new MistralAdapter(Http(fake, host));

        var embedded = await adapter.EmbedAsync(Profile(provider, host, model), "key-1234", Texts, TestContext.Current.CancellationToken);

        Assert.Equal([[0.1f, 0.2f], [0.3f, 0.4f]], embedded.Vectors.Select(v => v.ToArray()));
        Assert.Equal(5, embedded.TokensIn);
        Assert.Equal($"https://{host}/v1/embeddings", fake.Uri!.ToString());
        Assert.Equal("Bearer key-1234", fake.Authorization);
        Assert.Equal(model, fake.Body!["model"]!.GetValue<string>());
        Assert.Equal(Texts, fake.Body["input"]!.AsArray().Select(t => t!.GetValue<string>()));
    }

    [Fact]
    public async Task Gemini_embeds_a_batch_with_one_request_per_text()
    {
        var fake = new FakeProvider(HttpStatusCode.OK, """
            {"embeddings":[{"values":[0.1,0.2]},{"values":[0.3,0.4]}],"usageMetadata":{"promptTokenCount":4}}
            """);
        const string host = "generativelanguage.googleapis.com";

        var embedded = await new GeminiAdapter(Http(fake, host)).EmbedAsync(Profile("gemini", host, "gemini-embedding-001"), "key-1234", Texts,
            TestContext.Current.CancellationToken);

        Assert.Equal([[0.1f, 0.2f], [0.3f, 0.4f]], embedded.Vectors.Select(v => v.ToArray()));
        Assert.Equal(4, embedded.TokensIn);
        Assert.Equal($"https://{host}/v1beta/models/gemini-embedding-001:batchEmbedContents", fake.Uri!.ToString());
        Assert.Equal("key-1234", fake.ApiKey);
        var requests = fake.Body!["requests"]!.AsArray();
        Assert.Equal(["models/gemini-embedding-001", "models/gemini-embedding-001"], requests.Select(r => r!["model"]!.GetValue<string>()));
        Assert.Equal(Texts, requests.Select(r => r!["content"]!["parts"]![0]!["text"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "{}", CloudFailure.Transient, JobCodes.CloudFailed)]
    [InlineData(HttpStatusCode.Unauthorized, "{}", CloudFailure.Refused, CloudCodes.InvalidKey)]
    [InlineData(HttpStatusCode.BadRequest, "{}", CloudFailure.Refused, JobCodes.CloudRefused)]
    // A vector missing, or an answer that is not JSON: refused, never half a batch.
    [InlineData(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[0.1]}]}""", CloudFailure.Refused, JobCodes.CloudFailed)]
    [InlineData(HttpStatusCode.OK, "<html>", CloudFailure.Refused, JobCodes.CloudFailed)]
    public async Task A_failure_is_worded_as_for_a_chat_turn(HttpStatusCode status, string body, CloudFailure kind, string code)
    {
        var fake = new FakeProvider(status, body);

        var failed = await Assert.ThrowsAsync<CloudCallException>(() => new OpenAiAdapter(Http(fake, "api.openai.com"))
            .EmbedAsync(Profile("openai", "api.openai.com", "text-embedding-3-small"), "key", Texts, TestContext.Current.CancellationToken));

        Assert.Equal((kind, code), (failed.Kind, failed.Code));
    }

    [Fact]
    public async Task Gemini_refuses_an_answer_with_fewer_vectors_than_texts()
    {
        var fake = new FakeProvider(HttpStatusCode.OK, """{"embeddings":[{"values":[0.1]}]}""");
        const string host = "generativelanguage.googleapis.com";

        await Assert.ThrowsAsync<CloudCallException>(() => new GeminiAdapter(Http(fake, host))
            .EmbedAsync(Profile("gemini", host, "gemini-embedding-001"), "key", Texts, TestContext.Current.CancellationToken));
    }

    private static CloudHttp Http(FakeProvider fake, string host) => new(new HttpClient(CloudHttp.Handler([host], fake)));

    private static LlmProfile Profile(string provider, string host, string model)
        => new($"{provider}-embed", provider, "paid", host, model, [ModelUses.Embed],
            new DataTerms(new Dictionary<string, bool> { ["eea_ch_uk"] = false, ["other"] = false }, false, 30, [], "https://example.test/terms",
                new LocalDate(2026, 11, 22)),
            Pricing: new Pricing(0.1m, 0, "USD"), ContextTokens: 8192);

    private sealed class FakeProvider(HttpStatusCode status, string answer) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }

        public string? Authorization { get; private set; }

        public string? ApiKey { get; private set; }

        public JsonObject? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            ApiKey = request.Headers.TryGetValues("x-goog-api-key", out var keys) ? keys.Single() : null;
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }
}
