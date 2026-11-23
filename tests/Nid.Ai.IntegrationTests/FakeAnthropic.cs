using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Nid.Ai.IntegrationTests;

/// <summary>
/// Anthropic's Messages API as the adapter sees it over HTTP: each call takes the next answer queued (a status, or a stream
/// of events), and every request is kept with its headers. Never the real service.
/// </summary>
public sealed class FakeAnthropic : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _answers = new();

    public List<(Uri Uri, Dictionary<string, string> Headers, JsonObject Body)> Requests { get; } = [];

    public void Status(HttpStatusCode status) => _answers.Enqueue(() => new HttpResponseMessage(status));

    /// <summary>A turn: the text in pieces, an optional tool call, then the usage and the stop reason.</summary>
    public void Turn(string[] text, (string Id, string Name, string[] Json)? tool = null, string stop = "end_turn", int tokensIn = 20, int tokensOut = 5)
    {
        var events = new List<string>
        {
            """{"type":"message_start","message":{"id":"msg_1","model":"claude-opus-5-5","usage":{"input_tokens":""" + tokensIn + ""","output_tokens":1}}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
        };
        events.AddRange(text.Select(t => new JsonObject
        {
            ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = t },
        }.ToJsonString()));
        events.Add("""{"type":"content_block_stop","index":0}""");
        if (tool is { } call)
        {
            events.Add(new JsonObject
            {
                ["type"] = "content_block_start", ["index"] = 1,
                ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = call.Id, ["name"] = call.Name, ["input"] = new JsonObject() },
            }.ToJsonString());
            events.AddRange(call.Json.Select(j => new JsonObject
            {
                ["type"] = "content_block_delta", ["index"] = 1, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = j },
            }.ToJsonString()));
            events.Add("""{"type":"content_block_stop","index":1}""");
        }

        events.Add($$$"""{"type":"message_delta","delta":{"stop_reason":"{{{stop}}}"},"usage":{"output_tokens":{{{tokensOut}}}}}""");
        events.Add("""{"type":"message_stop"}""");
        Stream(events);
    }

    /// <summary>A turn cut by an error event (<paramref name="error"/>) after its first pieces of text.</summary>
    public void Cut(string[] text, string error)
        => Stream([
            """{"type":"message_start","message":{"id":"msg_1","model":"claude-opus-5-5","usage":{"input_tokens":20,"output_tokens":1}}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            .. text.Select(t => new JsonObject
            {
                ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = t },
            }.ToJsonString()),
            new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = error, ["message"] = "Overloaded" } }.ToJsonString(),
        ]);

    private void Stream(IEnumerable<string> events)
    {
        var body = string.Concat(events.Select(e => $"event: {JsonNode.Parse(e)!["type"]}\ndata: {e}\n\n"));
        _answers.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        Requests.Add((request.RequestUri!, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)), body));
        return _answers.Count > 0 ? _answers.Dequeue()() : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
    }
}
