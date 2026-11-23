using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Infrastructure.Cloud;
using Nid.Ai.Protocol.Contracts;
using Nid.Ai.Worker.Tests;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Ai.IntegrationTests;

/// <summary>#489: OpenAI, Mistral and Gemini behind the same gateway: each request in its provider's shape, with its
/// retention switch, and each stream read back as text, whole tool calls and the usage.</summary>
public sealed class ProviderTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Key = "provider-test-key-0123456789-WXYZ";
    private static readonly ChatTool Envelopes = new("list_envelopes", "Les enveloppes.", JsonDocument.Parse("""{"type":"object"}""").RootElement);
    private static readonly IReadOnlyList<ChatMessage> Hello = [new(ChatRole.System, "Tu es l'assistant de Nid."), new(ChatRole.User, "Bonjour")];

    [Theory]
    [InlineData("openai", "api.openai.com", "gpt-5.4-mini")]
    [InlineData("mistral", "api.mistral.ai", "mistral-large-4-0")]
    public async Task A_chat_completions_provider_streams_text_a_tool_call_and_the_usage(string provider, string host, string model)
    {
        var engine = new FakeEngine();
        engine.Chunks.AddRange([
            FakeEngine.TextChunk("Je regarde."), FakeEngine.ToolChunk(0, "call_1", "list_envelopes", "{\"own"), FakeEngine.ToolChunk(0, null, null, "er\":1}"),
            FakeEngine.FinishChunk("tool_calls"), FakeEngine.UsageChunk(40, 12, model),
        ]);
        var profile = Profile(provider, host, model);
        await using var app = await StartAsync(profile, engine);
        await EnableAsync(app, profile);

        var outputs = await ChatAsync(app, Hello, [Envelopes]);

        Assert.Equal("Je regarde.", string.Concat(outputs.Select(o => o.Text)));
        Assert.Equal(new ChatToolCall("call_1", "list_envelopes", "{\"owner\":1}"), Assert.Single(outputs, o => o.ToolCall is not null).ToolCall);
        Assert.Equal(new ChatUsage(40, 12, ChatStop.ToolCalls, model), outputs[^1].Usage);
        var sent = engine.ChatRequests.Single();
        Assert.Equal($"Bearer {Key}", engine.Authorizations.Single());
        Assert.Equal(model, sent["model"]!.GetValue<string>());
        Assert.Equal("system", sent["messages"]![0]!["role"]!.GetValue<string>());
        if (provider == "openai")
        {
            Assert.False(sent["store"]!.GetValue<bool>()); // decision U
            Assert.Equal(256, sent["max_completion_tokens"]!.GetValue<int>());
            Assert.True(sent["stream_options"]!["include_usage"]!.GetValue<bool>());
        }
        else
        {
            Assert.False(sent.ContainsKey("store"));
            Assert.Equal(256, sent["max_tokens"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task Gemini_streams_a_turn_and_gets_its_call_and_signature_back_on_the_next()
    {
        var gemini = new FakeGemini();
        gemini.Chunks.AddRange([
            """{"candidates":[{"content":{"role":"model","parts":[{"text":"Je regarde."}]}}],"modelVersion":"gemini-3.8-flash"}""",
            """{"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"id":"fc_1","name":"list_envelopes","args":{"owner":1}},"thoughtSignature":"sig-abc"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":50,"candidatesTokenCount":10,"thoughtsTokenCount":5}}""",
        ]);
        var profile = Profile("gemini", "generativelanguage.googleapis.com", "gemini-3.8-flash");
        await using var app = await StartAsync(profile, gemini);
        await EnableAsync(app, profile);

        var outputs = await ChatAsync(app, Hello, [Envelopes]);

        var call = Assert.Single(outputs, o => o.ToolCall is not null).ToolCall!;
        Assert.Equal(new ChatToolCall("fc_1", "list_envelopes", "{\"owner\":1}", "sig-abc"), call);
        Assert.Equal(new ChatUsage(50, 15, ChatStop.ToolCalls, "gemini-3.8-flash"), outputs[^1].Usage);
        var (path, headers, first) = gemini.Requests.Single();
        Assert.Equal("/v1beta/models/gemini-3.8-flash:streamGenerateContent", path);
        Assert.Equal(Key, headers["x-goog-api-key"]);
        Assert.Equal("Tu es l'assistant de Nid.", first["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("list_envelopes", first["tools"]![0]!["functionDeclarations"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(256, first["generationConfig"]!["maxOutputTokens"]!.GetValue<int>());

        gemini.Chunks.Clear();
        gemini.Chunks.Add("""{"candidates":[{"content":{"role":"model","parts":[{"text":"Vous avez une enveloppe."}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":70,"candidatesTokenCount":6}}""");
        await ChatAsync(app, [.. Hello, new ChatMessage(ChatRole.Assistant, null, [call]), new ChatMessage(ChatRole.Tool, "[{\"name\":\"Livret A\"}]", ToolCallId: "fc_1")], [Envelopes]);

        var next = gemini.Requests[^1].Body["contents"]!.AsArray();
        Assert.Equal("sig-abc", next[1]!["parts"]![0]!["thoughtSignature"]!.GetValue<string>());
        Assert.Equal("model", next[1]!["role"]!.GetValue<string>());
        var answer = next[2]!["parts"]![0]!["functionResponse"]!;
        Assert.Equal(("list_envelopes", "fc_1"), (answer["name"]!.GetValue<string>(), answer["id"]!.GetValue<string>()));
        Assert.Equal("[{\"name\":\"Livret A\"}]", answer["response"]!["result"]!.GetValue<string>());
    }

    [Fact]
    public void The_shipped_profiles_load()
    {
        var shipped = new LlmProfileLoader(NullLogger<LlmProfileLoader>.Instance).All.Select(p => (p.Id, p.Provider)).ToList();

        Assert.Contains(("anthropic-claude", "anthropic"), shipped);
        Assert.Contains(("openai-gpt", "openai"), shipped);
        Assert.Contains(("gemini-flash", "gemini"), shipped);
        Assert.Contains(("mistral-large", "mistral"), shipped);
    }

    private static LlmProfile Profile(string provider, string host, string model)
        => LlmProfileLoader.Read($$$"""
            {"id":"{{{provider}}}-test","provider":"{{{provider}}}","tier":"paid","host":"{{{host}}}","model":"{{{model}}}","capabilities":["chat"],"contextTokens":200000,
             "dataTerms":{"trainsOnData":{"eea_ch_uk":false,"other":false},"humanReview":true,"retentionSwitches":[],
             "termsUrl":"https://example.test/terms","reviewedOn":"2026-11-22"},
             "pricing":{"inputPerMillion":1,"outputPerMillion":2,"currency":"EUR"}}
            """)!;

    private Task<NidApp> StartAsync(LlmProfile profile, HttpMessageHandler server)
        => NidApp.StartAsync(fixture, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ILlmProfiles>(new TestProfiles([profile])));
            services.Replace(ServiceDescriptor.Singleton(new CloudHttp(new HttpClient(CloudHttp.Handler([profile.Host], server)))));
        });

    private sealed class TestProfiles(IReadOnlyList<LlmProfile> all) : ILlmProfiles
    {
        public IReadOnlyList<LlmProfile> All => all;
    }

    private static async Task EnableAsync(NidApp app, LlmProfile profile)
    {
        await app.RawAsync(app.AdultB, HttpMethod.Put, "/api/v1/ai/budget", new { monthlyBudgetEur = "10" });
        await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/cloud/region", new { region = "eea_ch_uk" });
        await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/ai/cloud/keys/{profile.Provider}", new { key = Key });
        Assert.Equal(HttpStatusCode.OK,
            (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/ai/cloud/profiles/{profile.Id}/enable", new { termsReviewedOn = "2026-11-22" })).StatusCode);
        await app.RawAsync(app.AdultB, HttpMethod.Put, "/api/v1/ai/settings/uses/chat", new { mode = "self_hosted_then_cloud" });
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<IAiSettings>().SetUseModelAsync(ModelUses.Chat, "llama3.2", TestContext.Current.CancellationToken);
    }

    private static async Task<List<ChatRequestOutput>> ChatAsync(NidApp app, IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools)
    {
        var outputs = new List<ChatRequestOutput>();
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await foreach (var output in scope.ServiceProvider.GetRequiredService<ILlmGateway>()
                           .ChatAsync(new LlmChatRequest(ModelUses.Chat, messages, tools, 256, "figures"), TestContext.Current.CancellationToken))
            outputs.Add(output.OrThrow());
        return outputs;
    }

    /// <summary>The Gemini API over HTTP: each call answers the queued chunks as server-sent events.</summary>
    private sealed class FakeGemini : HttpMessageHandler
    {
        public List<string> Chunks { get; } = [];

        public List<(string Path, Dictionary<string, string> Headers, JsonObject Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)),
                JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject()));
            var body = string.Concat(Chunks.Select(c => $"data: {c}\r\n\r\n"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
        }
    }
}
