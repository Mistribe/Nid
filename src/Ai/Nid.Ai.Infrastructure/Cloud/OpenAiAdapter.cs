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
/// The OpenAI Chat Completions format (<c>POST /v1/chat/completions</c>, streamed), hand-written (#489). OpenAI and Mistral
/// share it: OpenAI forces <c>store: false</c> (decision U) and asks for the usage chunk; Mistral sends the usage with its
/// last chunk and takes <c>max_tokens</c>.
/// </summary>
public abstract class ChatCompletionsAdapter(CloudHttp cloud) : ICloudAdapter
{
    public abstract string Provider { get; }

    /// <summary>OpenAI: <c>max_completion_tokens</c>, <c>store: false</c>, <c>stream_options.include_usage</c>.</summary>
    protected abstract bool IsOpenAi { get; }

    public async IAsyncEnumerable<ChatRequestOutput> ChatAsync(
        LlmProfile profile, string key, ChatRequestInput input, [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://{profile.Host}/v1/chat/completions"))
        {
            Content = JsonContent.Create(CompletionsRequest.From(input, IsOpenAi), CompletionsJson.Default.CompletionsRequest),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
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
                throw CloudFailures.Of(response.StatusCode);

            var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Arguments)>();
            var (tokensIn, tokensOut, stop, model, finished) = (0, 0, ChatStop.End, profile.Model, false);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(body, Encoding.UTF8);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;
                var data = line[5..].Trim();
                if (data == "[DONE]")
                    break;
                CompletionsChunk? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize(data, CompletionsJson.Default.CompletionsChunk);
                }
                catch (JsonException)
                {
                    throw new CloudCallException(CloudFailure.Refused, JobCodes.CloudFailed);
                }

                if (chunk is null)
                    continue;
                model = chunk.Model is { Length: > 0 and <= 200 } named ? named : model;
                if (chunk.Usage is { } usage)
                    (tokensIn, tokensOut) = (usage.PromptTokens, usage.CompletionTokens);
                foreach (var choice in chunk.Choices ?? [])
                {
                    if (choice.Delta?.Content is { Length: > 0 } text)
                        foreach (var piece in text.Chunk(32_768))
                            yield return new ChatRequestOutput(Text: new string(piece));
                    foreach (var call in choice.Delta?.ToolCalls ?? [])
                    {
                        var (id, name, arguments) = calls.TryGetValue(call.Index, out var known) ? known : (null, null, new StringBuilder());
                        calls[call.Index] = (call.Id ?? id, call.Function?.Name ?? name, arguments.Append(call.Function?.Arguments));
                    }

                    if (choice.FinishReason is { } reason)
                    {
                        finished = true;
                        stop = reason switch
                        {
                            "length" => ChatStop.Length,
                            "tool_calls" => ChatStop.ToolCalls,
                            "content_filter" => ChatStop.Refused,
                            _ => ChatStop.End,
                        };
                    }
                }
            }

            foreach (var (_, (id, name, arguments)) in calls)
            {
                var call = new ChatToolCall(id ?? $"call_{Guid.NewGuid():N}", name ?? "", arguments.Length == 0 ? "{}" : arguments.ToString());
                if (!call.IsValid())
                    throw new CloudCallException(CloudFailure.Refused, JobCodes.CloudFailed);
                yield return new ChatRequestOutput(ToolCall: call);
            }

            // A stream that never said why it stopped was cut: no usage frame, the gateway records it so.
            if (finished)
                yield return new ChatRequestOutput(Usage: new ChatUsage(tokensIn, tokensOut, calls.Count > 0 && stop == ChatStop.End ? ChatStop.ToolCalls : stop, model));
        }
    }
}

public sealed class OpenAiAdapter(CloudHttp cloud) : ChatCompletionsAdapter(cloud)
{
    public override string Provider => "openai";

    protected override bool IsOpenAi => true;
}

public sealed class MistralAdapter(CloudHttp cloud) : ChatCompletionsAdapter(cloud)
{
    public override string Provider => "mistral";

    protected override bool IsOpenAi => false;
}

/// <summary>Which provider failures are worth another profile (§5.3), for every adapter.</summary>
internal static class CloudFailures
{
    public static CloudCallException Of(HttpStatusCode status)
        => (int)status is 408 or 409 or 429 or >= 500
            ? new CloudCallException(CloudFailure.Transient, JobCodes.CloudFailed)
            : new CloudCallException(CloudFailure.Refused, (int)status is 401 or 403 ? CloudCodes.InvalidKey : JobCodes.CloudRefused);
}

internal sealed record CompletionsRequest(
    string Model, IReadOnlyList<CompletionsMessage> Messages, IReadOnlyList<CompletionsTool>? Tools, int? MaxTokens, int? MaxCompletionTokens,
    bool Stream, CompletionsStreamOptions? StreamOptions, bool? Store)
{
    public static CompletionsRequest From(ChatRequestInput input, bool openAi) => new(
        input.Model,
        [.. input.Messages.Select(m => new CompletionsMessage(
            m.Role.ToString().ToLowerInvariant(), m.Content,
            m.ToolCalls?.Select(c => new CompletionsToolCall(0, c.Id, "function", new CompletionsFunctionCall(c.Name, c.Arguments))).ToList(),
            m.ToolCallId))],
        input.Tools.Count == 0 ? null : [.. input.Tools.Select(t => new CompletionsTool("function", new CompletionsFunction(t.Name, t.Description, t.Parameters)))],
        openAi ? null : input.MaxTokens,
        openAi ? input.MaxTokens : null,
        Stream: true,
        openAi ? new CompletionsStreamOptions(IncludeUsage: true) : null,
        openAi ? false : null);
}

internal sealed record CompletionsStreamOptions(bool IncludeUsage);

internal sealed record CompletionsMessage(string Role, string? Content, IReadOnlyList<CompletionsToolCall>? ToolCalls, string? ToolCallId);

internal sealed record CompletionsTool(string Type, CompletionsFunction Function);

internal sealed record CompletionsFunction(string Name, string Description, JsonElement Parameters);

internal sealed record CompletionsToolCall(int Index, string? Id, string? Type, CompletionsFunctionCall? Function);

internal sealed record CompletionsFunctionCall(string? Name, string? Arguments);

internal sealed record CompletionsChunk(string? Model, IReadOnlyList<CompletionsChoice>? Choices, CompletionsUsage? Usage);

internal sealed record CompletionsChoice(int Index, CompletionsDelta? Delta, string? FinishReason);

internal sealed record CompletionsDelta(string? Content, IReadOnlyList<CompletionsToolCall>? ToolCalls);

internal sealed record CompletionsUsage(int PromptTokens, int CompletionTokens);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CompletionsRequest))]
[JsonSerializable(typeof(CompletionsChunk))]
internal sealed partial class CompletionsJson : JsonSerializerContext;
