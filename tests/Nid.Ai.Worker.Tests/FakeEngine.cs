using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Nid.Ai.Worker.Tests;

/// <summary>
/// An OpenAI-compatible engine as the worker sees it over HTTP (Ollama, llama.cpp, vLLM, LM Studio): <c>/v1/models</c> and a
/// streamed <c>/v1/chat/completions</c>. Shared with <c>Nid.Ai.IntegrationTests</c>; it knows nothing of the worker's types.
/// </summary>
public sealed class FakeEngine : HttpMessageHandler
{
    /// <summary>The <c>data</c> entries of <c>/v1/models</c>.</summary>
    public List<JsonObject> Models { get; } = [new JsonObject { ["id"] = "llama3.2", ["object"] = "model", ["created"] = 1, ["owned_by"] = "library" }];

    /// <summary>The <c>data:</c> lines of the next chat answer, <c>[DONE]</c> added.</summary>
    public List<string> Chunks { get; } = [];

    /// <summary>Set: every call answers this status, with no body.</summary>
    public HttpStatusCode? Fails { get; set; }

    /// <summary>Set: the engine cannot be reached.</summary>
    public bool Down { get; set; }

    public List<JsonObject> ChatRequests { get; } = [];

    /// <summary>#558: the bodies of <c>/v1/embeddings</c>, and the length of each vector it answers (the first value is the
    /// text's length, the others the text's place in the batch plus a tenth per position).</summary>
    public List<JsonObject> EmbedRequests { get; } = [];

    public int EmbedDimension { get; set; } = 4;

    /// <summary>The <c>Authorization</c> header of each chat request (a cloud provider's key; none for an engine).</summary>
    public List<string?> Authorizations { get; } = [];

    public static string TextChunk(string text) => $$$"""{"choices":[{"index":0,"delta":{"content":{{{JsonValue.Create(text).ToJsonString()}}}}}]}""";

    public static string ToolChunk(int index, string? id, string? name, string arguments)
        => new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = new JsonObject
                {
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["index"] = index, ["id"] = id, ["type"] = id is null ? null : "function",
                        ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
                    }),
                },
            }),
        }.ToJsonString();

    public static string FinishChunk(string reason) => $$$"""{"choices":[{"index":0,"delta":{},"finish_reason":"{{{reason}}}"}]}""";

    public static string UsageChunk(int prompt, int completion, string model = "llama3.2")
        => $$$"""{"model":"{{{model}}}","choices":[],"usage":{"prompt_tokens":{{{prompt}}},"completion_tokens":{{{completion}}}}}""";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down)
            throw new HttpRequestException("Connection refused", null, HttpStatusCode.ServiceUnavailable);
        if (Fails is { } status)
            return new HttpResponseMessage(status);

        switch (request.RequestUri!.AbsolutePath)
        {
            case "/v1/models":
                return Json(new JsonObject { ["object"] = "list", ["data"] = new JsonArray([.. Models.Select(m => m.DeepClone())]) });
            case "/v1/embeddings":
                var embed = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                EmbedRequests.Add(embed);
                var texts = embed["input"]!.AsArray().Select(t => t!.GetValue<string>()).ToList();
                return Json(new JsonObject
                {
                    ["object"] = "list",
                    ["model"] = embed["model"]!.GetValue<string>(),
                    ["data"] = new JsonArray([.. texts.Select((text, i) => (JsonNode)new JsonObject
                    {
                        ["object"] = "embedding", ["index"] = i,
                        ["embedding"] = new JsonArray([.. Enumerable.Range(0, EmbedDimension).Select(d => (JsonNode)JsonValue.Create(d == 0 ? text.Length : i + d / 10.0)!)]),
                    })]),
                    ["usage"] = new JsonObject { ["prompt_tokens"] = texts.Sum(t => t.Length), ["total_tokens"] = texts.Sum(t => t.Length) },
                });
            case "/v1/chat/completions":
                ChatRequests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());
                Authorizations.Add(request.Headers.Authorization?.ToString());
                var body = string.Concat(Chunks.Select(c => $"data: {c}\n\n")) + "data: [DONE]\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static HttpResponseMessage Json(JsonNode body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
