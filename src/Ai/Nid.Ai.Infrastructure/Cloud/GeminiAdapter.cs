using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nid.Ai.Application;
using Nid.Ai.Protocol.Contracts;

namespace Nid.Ai.Infrastructure.Cloud;

/// <summary>
/// The Gemini API (<c>POST /v1beta/models/{model}:streamGenerateContent?alt=sse</c>, <c>x-goog-api-key</c>), hand-written
/// (#489). System messages go to <c>systemInstruction</c>; the assistant is <c>model</c>; a tool's answer is a
/// <c>functionResponse</c> part named after its call. Gemini 3 wants each call's <c>thoughtSignature</c> back on the next
/// turn: it travels in <see cref="ChatToolCall.Signature"/>. Temperature is ignored by these models: not sent.
/// </summary>
public sealed class GeminiAdapter(CloudHttp cloud) : ICloudAdapter
{
    private static readonly HashSet<string> Refusals = ["SAFETY", "PROHIBITED_CONTENT", "BLOCKLIST", "SPII", "RECITATION", "IMAGE_SAFETY"];

    public string Provider => "gemini";

    public async IAsyncEnumerable<ChatRequestOutput> ChatAsync(
        LlmProfile profile, string key, ChatRequestInput input, [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri($"https://{profile.Host}/v1beta/models/{Uri.EscapeDataString(input.Model)}:streamGenerateContent?alt=sse"))
        {
            Content = JsonContent.Create(GeminiRequest.From(input), GeminiJson.Default.GeminiRequest),
        };
        request.Headers.Add("x-goog-api-key", key);

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

            var (tokensIn, tokensOut, stop, model, finished, calls) = (0, 0, ChatStop.End, profile.Model, false, 0);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(body, Encoding.UTF8);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;
                GeminiResponse? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize(line[5..].Trim(), GeminiJson.Default.GeminiResponse);
                }
                catch (JsonException)
                {
                    throw new CloudCallException(CloudFailure.Refused, JobCodes.CloudFailed);
                }

                if (chunk is null)
                    continue;
                model = chunk.ModelVersion is { Length: > 0 and <= 200 } version ? version : model;
                if (chunk.UsageMetadata is { } usage)
                    (tokensIn, tokensOut) = (usage.PromptTokenCount, usage.CandidatesTokenCount + usage.ThoughtsTokenCount);
                if (chunk.PromptFeedback?.BlockReason is not null)
                    (finished, stop) = (true, ChatStop.Refused);
                foreach (var candidate in chunk.Candidates?.Take(1) ?? [])
                {
                    foreach (var part in candidate.Content?.Parts ?? [])
                    {
                        if (part.Thought == true)
                            continue; // its reasoning, not its answer
                        if (part.Text is { Length: > 0 } text)
                            foreach (var piece in text.Chunk(32_768))
                                yield return new ChatRequestOutput(Text: new string(piece));
                        if (part.FunctionCall is { Name: { } name } call)
                        {
                            var toolCall = new ChatToolCall(call.Id ?? $"gemini_{Guid.NewGuid():N}", name,
                                call.Args is { ValueKind: JsonValueKind.Object } args ? args.GetRawText() : "{}", part.ThoughtSignature);
                            if (!toolCall.IsValid())
                                throw new CloudCallException(CloudFailure.Refused, JobCodes.CloudFailed);
                            calls++;
                            yield return new ChatRequestOutput(ToolCall: toolCall);
                        }
                    }

                    if (candidate.FinishReason is { } reason)
                    {
                        finished = true;
                        stop = reason == "MAX_TOKENS" ? ChatStop.Length : Refusals.Contains(reason) ? ChatStop.Refused : ChatStop.End;
                    }
                }
            }

            if (finished)
                yield return new ChatRequestOutput(Usage: new ChatUsage(tokensIn, tokensOut, calls > 0 && stop == ChatStop.End ? ChatStop.ToolCalls : stop, model));
        }
    }
}

internal sealed record GeminiRequest(GeminiContent? SystemInstruction, IReadOnlyList<GeminiContent> Contents, IReadOnlyList<GeminiTools>? Tools,
    GeminiGeneration GenerationConfig)
{
    public static GeminiRequest From(ChatRequestInput input)
    {
        var system = string.Join("\n\n", input.Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Content));
        // A tool's answer names its function: the name comes from the call that asked for it.
        var names = input.Messages.SelectMany(m => m.ToolCalls ?? []).GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Name);
        var contents = new List<GeminiContent>();
        foreach (var message in input.Messages.Where(m => m.Role != ChatRole.System))
        {
            IReadOnlyList<GeminiPart> parts = message.Role switch
            {
                ChatRole.Tool => [new GeminiPart(FunctionResponse: new GeminiFunctionResponse(
                    names.GetValueOrDefault(message.ToolCallId!, "tool"), Generated(message.ToolCallId) ? null : message.ToolCallId, Result(message.Content)))],
                ChatRole.Assistant => [.. (message.Content is { Length: > 0 } text ? [new GeminiPart(Text: text)] : Array.Empty<GeminiPart>()),
                    .. (message.ToolCalls ?? []).Select(c => new GeminiPart(
                        FunctionCall: new GeminiFunctionCall(Generated(c.Id) ? null : c.Id, c.Name, Arguments(c.Arguments)), ThoughtSignature: c.Signature))],
                _ => [new GeminiPart(Text: message.Content)],
            };
            var role = message.Role == ChatRole.Assistant ? "model" : "user";
            if (message.Role == ChatRole.Tool && contents is [.., { Role: "user" } last] && last.Parts.All(p => p.FunctionResponse is not null))
                contents[^1] = last with { Parts = [.. last.Parts, .. parts] };
            else
                contents.Add(new GeminiContent(role, parts));
        }

        return new GeminiRequest(
            system.Length > 0 ? new GeminiContent(null, [new GeminiPart(Text: system)]) : null,
            contents,
            input.Tools.Count == 0 ? null : [new GeminiTools([.. input.Tools.Select(t => new GeminiDeclaration(t.Name, t.Description, t.Parameters))])],
            new GeminiGeneration(input.MaxTokens));
    }

    /// <summary>An id Nid made up because Gemini gave none: not sent back as Gemini's.</summary>
    private static bool Generated(string? id) => id?.StartsWith("gemini_", StringComparison.Ordinal) == true;

    private static JsonElement Arguments(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : Empty();
        }
        catch (JsonException)
        {
            return Empty();
        }
    }

    /// <summary>A tool's answer as the object Gemini wants: the answer itself when it is one, else <c>{"result": …}</c>.</summary>
    private static JsonElement Result(string? content)
    {
        if (content is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // plain text: wrapped below
            }
        }

        return JsonSerializer.SerializeToElement(new GeminiResult(content ?? ""), GeminiJson.Default.GeminiResult);
    }

    private static JsonElement Empty() => JsonDocument.Parse("{}").RootElement.Clone();
}

internal sealed record GeminiResult(string Result);

internal sealed record GeminiContent(string? Role, IReadOnlyList<GeminiPart> Parts);

internal sealed record GeminiPart(
    string? Text = null, GeminiFunctionCall? FunctionCall = null, GeminiFunctionResponse? FunctionResponse = null, string? ThoughtSignature = null,
    bool? Thought = null);

internal sealed record GeminiFunctionCall(string? Id, string? Name, JsonElement? Args);

internal sealed record GeminiFunctionResponse(string Name, string? Id, JsonElement Response);

internal sealed record GeminiTools(IReadOnlyList<GeminiDeclaration> FunctionDeclarations);

internal sealed record GeminiDeclaration(string Name, string Description, JsonElement ParametersJsonSchema);

internal sealed record GeminiGeneration(int MaxOutputTokens);

internal sealed record GeminiResponse(IReadOnlyList<GeminiCandidate>? Candidates, GeminiUsage? UsageMetadata, string? ModelVersion, GeminiFeedback? PromptFeedback);

internal sealed record GeminiCandidate(GeminiContent? Content, string? FinishReason);

internal sealed record GeminiUsage(int PromptTokenCount, int CandidatesTokenCount, int ThoughtsTokenCount);

internal sealed record GeminiFeedback(string? BlockReason);

/// <summary>The Gemini API's wire format: camelCase, nulls left out, unknown fields ignored.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GeminiRequest))]
[JsonSerializable(typeof(GeminiResponse))]
[JsonSerializable(typeof(GeminiResult))]
internal sealed partial class GeminiJson : JsonSerializerContext;
