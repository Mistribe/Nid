using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nid.Ai.Application;
using Nid.Ai.Protocol.Contracts;

namespace Nid.Ai.Infrastructure.Cloud;

/// <summary>
/// The client every cloud adapter calls through (#487): HTTPS to a loaded profile's host only, on Myelin's address checks
/// (no private address, no DNS rebinding). <c>SafeHttpClient</c> reads whole bodies; a chat turn streams, hence this client
/// (<c>docs/ai/README.md</c> §3.5).
/// </summary>
public sealed class CloudHttp(HttpClient http)
{
    public HttpClient Http { get; } = http;

    public static HttpMessageHandler Handler(IEnumerable<string> hosts, HttpMessageHandler inner)
        => new ProfileHostsOnly(hosts.ToHashSet(StringComparer.OrdinalIgnoreCase)) { InnerHandler = inner };

    /// <summary>Refuses any request that is not HTTPS to a profile's host, before it leaves.</summary>
    private sealed class ProfileHostsOnly(HashSet<string> hosts) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => request.RequestUri is { Scheme: "https" } uri && hosts.Contains(uri.Host) && uri.IsDefaultPort
                ? base.SendAsync(request, cancellationToken)
                : throw new HttpRequestException($"{request.RequestUri?.Host} is not the host of a cloud profile.");
    }
}

/// <summary>
/// Anthropic's Messages API (<c>POST /v1/messages</c>, <c>anthropic-version: 2026-10-21</c>), streamed, hand-written: the
/// neutral chat turn in, text, whole tool calls and the usage out. That API version refuses sampling settings, so the
/// temperature is not sent.
/// </summary>
public sealed class AnthropicAdapter(CloudHttp cloud) : ICloudAdapter
{
    public const string Version = "2026-10-21";

    public string Provider => "anthropic";

    public async IAsyncEnumerable<ChatRequestOutput> ChatAsync(
        LlmProfile profile, string key, ChatRequestInput input, [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://{profile.Host}/v1/messages"))
        {
            Content = JsonContent.Create(AnthropicRequest.From(input), AnthropicJson.Default.AnthropicRequest),
        };
        request.Headers.Add("x-api-key", key);
        request.Headers.Add("anthropic-version", Version);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try
        {
            response = await cloud.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            throw new CloudCallException(CloudFailure.Transient, JobCodes.CloudFailed);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw Failure(response.StatusCode);

            var blocks = new Dictionary<int, (string Id, string Name, StringBuilder Input)>();
            var (tokensIn, tokensOut, stop, model) = (0, 0, ChatStop.End, profile.Model);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(body, Encoding.UTF8);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;
                AnthropicEvent? e;
                try
                {
                    e = JsonSerializer.Deserialize(line[5..].Trim(), AnthropicJson.Default.AnthropicEvent);
                }
                catch (JsonException)
                {
                    throw new CloudCallException(CloudFailure.Refused, JobCodes.CloudFailed);
                }

                switch (e?.Type)
                {
                    case "message_start" when e.Message is { } message:
                        model = message.Model ?? model;
                        tokensIn = (message.Usage?.InputTokens ?? 0) + (message.Usage?.CacheCreationInputTokens ?? 0) + (message.Usage?.CacheReadInputTokens ?? 0);
                        break;
                    case "content_block_start" when e.ContentBlock is { Type: "tool_use", Id: { } id, Name: { } name }:
                        blocks[e.Index] = (id, name, new StringBuilder());
                        break;
                    case "content_block_delta" when e.Delta is { Type: "text_delta", Text: { Length: > 0 } text }:
                        foreach (var piece in text.Chunk(32_768))
                            yield return new ChatRequestOutput(Text: new string(piece));
                        break;
                    case "content_block_delta" when e.Delta is { Type: "input_json_delta", PartialJson: { } json } && blocks.TryGetValue(e.Index, out var block):
                        block.Input.Append(json);
                        break;
                    case "content_block_stop" when blocks.Remove(e.Index, out var done):
                        var call = new ChatToolCall(done.Id, done.Name, done.Input.Length == 0 ? "{}" : done.Input.ToString());
                        if (!call.IsValid())
                            throw new CloudCallException(CloudFailure.Refused, JobCodes.CloudFailed);
                        yield return new ChatRequestOutput(ToolCall: call);
                        break;
                    case "message_delta":
                        tokensOut = e.Usage?.OutputTokens ?? tokensOut;
                        stop = e.Delta?.StopReason switch
                        {
                            "max_tokens" => ChatStop.Length,
                            "tool_use" => ChatStop.ToolCalls,
                            "refusal" => ChatStop.Refused,
                            _ => ChatStop.End,
                        };
                        break;
                    case "error":
                        // An error inside the stream (overloaded, for one): worth another profile only before any output.
                        throw new CloudCallException(e.Error?.Type is "overloaded_error" or "api_error" or "rate_limit_error"
                            ? CloudFailure.Transient : CloudFailure.Refused, JobCodes.CloudFailed);
                    case "message_stop":
                        yield return new ChatRequestOutput(Usage: new ChatUsage(tokensIn, tokensOut, stop, model));
                        yield break;
                }
            }

            // The stream ended without message_stop: cut, and the gateway records it so.
        }
    }

    /// <summary>A status worth another profile (quota, rate, overload, outage) or not (the request itself, the key).</summary>
    private static CloudCallException Failure(HttpStatusCode status)
        => (int)status is 408 or 409 or 429 or >= 500
            ? new CloudCallException(CloudFailure.Transient, JobCodes.CloudFailed)
            : new CloudCallException(CloudFailure.Refused, (int)status is 401 or 403 ? CloudCodes.InvalidKey : JobCodes.CloudRefused);
}

internal sealed record AnthropicRequest(
    string Model, int MaxTokens, string? System, IReadOnlyList<AnthropicMessage> Messages, IReadOnlyList<AnthropicTool>? Tools, bool Stream)
{
    /// <summary>System messages join the top-level <c>system</c>; consecutive tool answers go in one user message, as the API
    /// wants all the results of one turn together.</summary>
    public static AnthropicRequest From(ChatRequestInput input)
    {
        var system = string.Join("\n\n", input.Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Content));
        var messages = new List<AnthropicMessage>();
        foreach (var message in input.Messages.Where(m => m.Role != ChatRole.System))
        {
            var blocks = message.Role switch
            {
                ChatRole.Tool => [new AnthropicBlock("tool_result", ToolUseId: message.ToolCallId, Content: message.Content)],
                ChatRole.Assistant => [.. (message.Content is { Length: > 0 } text ? [new AnthropicBlock("text", Text: text)] : Array.Empty<AnthropicBlock>()),
                    .. (message.ToolCalls ?? []).Select(c => new AnthropicBlock("tool_use", Id: c.Id, Name: c.Name, Input: Arguments(c.Arguments)))],
                _ => new List<AnthropicBlock> { new("text", Text: message.Content) },
            };
            var role = message.Role == ChatRole.Assistant ? "assistant" : "user";
            if (message.Role == ChatRole.Tool && messages is [.., { Role: "user" } last] && last.Content.All(b => b.Type == "tool_result"))
                messages[^1] = last with { Content = [.. last.Content, .. blocks] };
            else
                messages.Add(new AnthropicMessage(role, blocks));
        }

        return new AnthropicRequest(input.Model, input.MaxTokens, system.Length > 0 ? system : null, messages,
            input.Tools.Count == 0 ? null : [.. input.Tools.Select(t => new AnthropicTool(t.Name, t.Description, t.Parameters))], Stream: true);
    }

    /// <summary>The arguments the model wrote, as the object it meant; anything else as an empty object.</summary>
    private static JsonElement Arguments(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();
}

internal sealed record AnthropicMessage(string Role, IReadOnlyList<AnthropicBlock> Content);

internal sealed record AnthropicBlock(
    string Type, string? Text = null, string? Id = null, string? Name = null, JsonElement? Input = null, string? ToolUseId = null, string? Content = null);

internal sealed record AnthropicTool(string Name, string Description, JsonElement InputSchema);

internal sealed record AnthropicEvent(
    string Type, int Index, AnthropicStartMessage? Message, AnthropicStartBlock? ContentBlock, AnthropicDelta? Delta, AnthropicUsage? Usage,
    AnthropicError? Error);

internal sealed record AnthropicStartMessage(string? Model, AnthropicUsage? Usage);

internal sealed record AnthropicStartBlock(string Type, string? Id, string? Name);

internal sealed record AnthropicDelta(string? Type, string? Text, string? PartialJson, string? StopReason);

internal sealed record AnthropicUsage(int? InputTokens, int? OutputTokens, int? CacheCreationInputTokens, int? CacheReadInputTokens);

internal sealed record AnthropicError(string? Type);

/// <summary>The Messages API wire format: snake_case, nulls left out, unknown fields ignored (the API adds its own).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AnthropicRequest))]
[JsonSerializable(typeof(AnthropicEvent))]
internal sealed partial class AnthropicJson : JsonSerializerContext;
