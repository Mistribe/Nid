using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Nid.Ai.Protocol.Contracts;

namespace Nid.Ai.Worker;

/// <summary>
/// The model engine behind the worker (#485, <c>docs/ai/README.md</c> §3): any runtime with the OpenAI-compatible API
/// (Ollama, llama.cpp, vLLM, LM Studio), on the worker's own network. Nid never sees its address. No <see cref="Url"/>: the
/// worker serves no model.
/// </summary>
public sealed class EngineOptions
{
    /// <summary>The engine's base address, e.g. <c>http://ollama:11434</c>.</summary>
    public string? Url { get; set; }

    /// <summary>Comma-separated models to serve; empty: every model the engine lists.</summary>
    public string? Models { get; set; }

    /// <summary>What the engine does not report: the context and output each model accepts, and whether it calls tools.</summary>
    public int ContextTokens { get; set; } = 8192;

    public int MaxOutputTokens { get; set; } = 4096;

    public bool Tools { get; set; } = true;
}

/// <summary>How the worker reaches its engine; a test replaces it with a fake engine.</summary>
public sealed record EngineTransport(HttpMessageHandler Handler);

/// <summary>The engine failed a request: <see cref="Code"/> ends the request's stream as its error frame.</summary>
public sealed class EngineException(string code) : Exception($"The engine failed ({code}).")
{
    public string Code { get; } = code;
}

/// <summary>The engine's OpenAI-compatible API: the models it serves (listed at most every 30 s), and a streamed turn.</summary>
public sealed partial class EngineClient : IDisposable
{
    private static readonly TimeSpan ListFor = TimeSpan.FromSeconds(30);

    private readonly EngineOptions _options;
    private readonly HttpClient? _http;
    private readonly TimeProvider _time;
    private readonly ILogger<EngineClient> _logger;
    private readonly SemaphoreSlim _listing = new(1, 1);
    private (DateTimeOffset At, IReadOnlyList<DeclaredModel> Models)? _listed;

    public EngineClient(IOptions<EngineOptions> options, EngineTransport transport, TimeProvider time, ILogger<EngineClient> logger)
    {
        _options = options.Value;
        _time = time;
        _logger = logger;
        if (_options.Url is { Length: > 0 } url)
            _http = new HttpClient(transport.Handler, disposeHandler: false) { BaseAddress = new Uri(url), Timeout = Timeout.InfiniteTimeSpan };
    }

    public bool Configured => _http is not null;

    /// <summary>The models to declare; none when the engine cannot be read, so Nid sends it nothing until it answers again.</summary>
    public async Task<IReadOnlyList<DeclaredModel>> ModelsAsync(CancellationToken ct)
    {
        if (_http is null)
            return [];
        await _listing.WaitAsync(ct);
        try
        {
            if (_listed is { } listed && _time.GetUtcNow() - listed.At < ListFor)
                return listed.Models;
            var models = await ListAsync(_http, ct);
            _listed = (_time.GetUtcNow(), models);
            return models;
        }
        finally
        {
            _listing.Release();
        }
    }

    private async Task<IReadOnlyList<DeclaredModel>> ListAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync("v1/models", ct);
            response.EnsureSuccessStatusCode();
            var list = await response.Content.ReadFromJsonAsync(EngineJson.Default.ModelList, ct);
            var allowed = _options.Models?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            return [.. (list?.Data ?? [])
                .Where(e => e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 and <= 200 })
                .Select(e => (Id: e.GetProperty("id").GetString()!, Entry: e))
                .Where(m => allowed is null || allowed.Count == 0 || allowed.Contains(m.Id))
                .Take(100)
                .Select(m => new DeclaredModel(DeclaredCapability.Chat, m.Id, Digest(m.Entry), _options.ContextTokens, _options.MaxOutputTokens,
                    Tools: _options.Tools, StructuredOutput: false))];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogListFailed(e.GetType().Name);
            return [];
        }
    }

    /// <summary>What the engine says of the model, hashed: the same model pulled again or swapped under its name reads as a
    /// change (<c>docs/ai/README.md</c> §3.3).</summary>
    private static string Digest(JsonElement entry) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(entry.GetRawText())));

    /// <summary>One turn, streamed: text as it comes, each tool call once whole, then the usage.</summary>
    public async IAsyncEnumerable<ChatRequestOutput> ChatAsync(ChatRequestInput input, [EnumeratorCancellation] CancellationToken ct)
    {
        if (_http is null)
            throw new EngineException("engine_unconfigured");

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = JsonContent.Create(EngineRequest.From(input), EngineJson.Default.EngineRequest),
        };
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            throw new EngineException("engine_unreachable");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new EngineException((int)response.StatusCode >= 500 ? "engine_failed" : "engine_refused");

            var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Arguments)>();
            ChatStop stop = ChatStop.End;
            EngineUsage? usage = null;
            string model = input.Model;
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(body, Encoding.UTF8);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;
                var data = line[5..].Trim();
                if (data == "[DONE]")
                    break;
                EngineChunk? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize(data, EngineJson.Default.EngineChunk);
                }
                catch (JsonException)
                {
                    throw new EngineException("engine_invalid");
                }

                if (chunk is null)
                    continue;
                if (chunk.Model is { Length: > 0 and <= 200 } named)
                    model = named;
                usage = chunk.Usage ?? usage;
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

                    stop = choice.FinishReason switch
                    {
                        "length" => ChatStop.Length,
                        "tool_calls" => ChatStop.ToolCalls,
                        "content_filter" => ChatStop.Refused,
                        "stop" => ChatStop.End,
                        _ => stop,
                    };
                }
            }

            foreach (var (_, (id, name, arguments)) in calls)
            {
                var call = new ChatToolCall(id ?? $"call_{Guid.NewGuid():N}", name ?? "", arguments.ToString());
                if (!call.IsValid())
                    throw new EngineException("engine_invalid");
                yield return new ChatRequestOutput(ToolCall: call);
            }

            if (calls.Count > 0 && stop == ChatStop.End)
                stop = ChatStop.ToolCalls;
            yield return new ChatRequestOutput(Usage: new ChatUsage(usage?.PromptTokens ?? 0, usage?.CompletionTokens ?? 0, stop, model));
        }
    }

    public void Dispose()
    {
        _http?.Dispose();
        _listing.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The engine's model list could not be read ({Error})")]
    private partial void LogListFailed(string error);
}

/// <summary>The test request's sibling for real work (<c>contracts/requests/chat/v1</c>): a turn through the engine.</summary>
public sealed class ChatRequest(EngineClient engine) : IRequestHandler
{
    public string Type => "chat";

    public int Version => 1;

    public bool Available => engine.Configured;

    public async IAsyncEnumerable<byte[]> RunAsync(byte[] input, [EnumeratorCancellation] CancellationToken ct)
    {
        var request = EchoJob.Read<ChatRequestInput>(input);
        await foreach (var output in engine.ChatAsync(request, ct))
            yield return ContractJson.Write(output);
    }
}

internal sealed record ModelList(IReadOnlyList<JsonElement>? Data);

internal sealed record EngineRequest(
    string Model,
    IReadOnlyList<EngineMessage> Messages,
    IReadOnlyList<EngineTool>? Tools,
    int MaxTokens,
    double? Temperature,
    bool Stream,
    EngineStreamOptions StreamOptions)
{
    public static EngineRequest From(ChatRequestInput input) => new(
        input.Model,
        [.. input.Messages.Select(m => new EngineMessage(
            m.Role.ToString().ToLowerInvariant(),
            m.Content,
            m.ToolCalls?.Select(c => new EngineToolCall(0, c.Id, "function", new EngineFunctionCall(c.Name, c.Arguments))).ToList(),
            m.ToolCallId))],
        input.Tools.Count == 0 ? null : [.. input.Tools.Select(t => new EngineTool("function", new EngineFunction(t.Name, t.Description, t.Parameters)))],
        input.MaxTokens,
        input.Temperature,
        Stream: true,
        new EngineStreamOptions(IncludeUsage: true));
}

internal sealed record EngineStreamOptions(bool IncludeUsage);

internal sealed record EngineMessage(string Role, string? Content, IReadOnlyList<EngineToolCall>? ToolCalls, string? ToolCallId);

internal sealed record EngineTool(string Type, EngineFunction Function);

internal sealed record EngineFunction(string Name, string Description, JsonElement Parameters);

internal sealed record EngineToolCall(int Index, string? Id, string? Type, EngineFunctionCall? Function);

internal sealed record EngineFunctionCall(string? Name, string? Arguments);

internal sealed record EngineChunk(string? Model, IReadOnlyList<EngineChoice>? Choices, EngineUsage? Usage);

internal sealed record EngineChoice(int Index, EngineDelta? Delta, string? FinishReason);

internal sealed record EngineDelta(string? Content, IReadOnlyList<EngineToolCall>? ToolCalls);

internal sealed record EngineUsage(int PromptTokens, int CompletionTokens);

/// <summary>The OpenAI wire format: snake_case, nulls left out, unknown fields ignored (engines add their own).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ModelList))]
[JsonSerializable(typeof(EngineRequest))]
[JsonSerializable(typeof(EngineChunk))]
internal sealed partial class EngineJson : JsonSerializerContext;
