using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nid.Ai.Protocol.Contracts;

/// <summary>
/// One chat turn (<c>contracts/requests/chat/v1</c>, #485): the model, the conversation so far, the tools the model may call
/// and the most it may write. One neutral shape that every provider maps, a worker's engine and each cloud adapter alike.
/// </summary>
public sealed record ChatRequestInput(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ChatTool> Tools,
    int MaxTokens,
    double? Temperature = null) : IContract
{
    public const int MaxMessages = 500;
    public const int MaxTools = 128;

    public bool IsValid()
        => Model.Length is > 0 and <= 200
           && Messages.Count is > 0 and <= MaxMessages && Messages.All(m => m?.IsValid() == true)
           && Tools.Count <= MaxTools && Tools.All(t => t?.IsValid() == true) && Tools.Select(t => t.Name).Distinct().Count() == Tools.Count
           && MaxTokens is > 0 and <= 131_072
           && Temperature is null or (>= 0 and <= 2);
}

public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>A message of the conversation. An assistant message carries text, tool calls or both; a tool message answers
/// one call (<see cref="ToolCallId"/>) with its result as text.</summary>
public sealed record ChatMessage(ChatRole Role, string? Content, IReadOnlyList<ChatToolCall>? ToolCalls = null, string? ToolCallId = null)
    : IContract
{
    public bool IsValid()
        => Content is null or { Length: <= 1_000_000 }
           && (ToolCalls is null || (Role == ChatRole.Assistant && ToolCalls.Count is > 0 and <= ChatRequestInput.MaxTools && ToolCalls.All(c => c?.IsValid() == true)))
           && (ToolCallId is null || (Role == ChatRole.Tool && ToolCallId.Length is > 0 and <= 200))
           && Role switch
           {
               ChatRole.System or ChatRole.User => Content is not null,
               ChatRole.Assistant => Content is not null || ToolCalls is not null,
               _ => Content is not null && ToolCallId is not null,
           };
}

/// <summary>A tool the model may call: its name, what it does, and its parameters as a JSON Schema object.</summary>
public sealed partial record ChatTool(string Name, string Description, JsonElement Parameters) : IContract
{
    public bool IsValid() => ToolName().IsMatch(Name) && Description.Length <= 4000 && Parameters.ValueKind == JsonValueKind.Object;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    internal static partial Regex ToolName();
}

/// <summary>A call the model asks for: its id, the tool, the arguments as the JSON text the model wrote (checked by the
/// tool loop, not here). <c>signature</c>: what a provider needs back with the call on the next turn (Gemini's
/// <c>thoughtSignature</c>), opaque to Nid (#489).</summary>
public sealed record ChatToolCall(string Id, string Name, string Arguments, string? Signature = null) : IContract
{
    public bool IsValid() => Id.Length is > 0 and <= 200 && ChatTool.ToolName().IsMatch(Name) && Arguments.Length <= 100_000 && Signature is null or { Length: <= 16_384 };
}

/// <summary>
/// One data frame of a turn: exactly one of a piece of text, a whole tool call, or the usage that ends the turn. The usage
/// frame comes last; a turn without one was cut.
/// </summary>
public sealed record ChatRequestOutput(string? Text = null, ChatToolCall? ToolCall = null, ChatUsage? Usage = null) : IContract
{
    public bool IsValid()
        => (Text is not null ? 1 : 0) + (ToolCall is not null ? 1 : 0) + (Usage is not null ? 1 : 0) == 1
           && Text is null or { Length: <= 32_768 }
           && ToolCall?.IsValid() != false
           && Usage?.IsValid() != false;
}

public enum ChatStop
{
    /// <summary>The model finished its answer.</summary>
    End,

    /// <summary>The answer reached <see cref="ChatRequestInput.MaxTokens"/>.</summary>
    Length,

    /// <summary>The model waits for the results of its tool calls.</summary>
    ToolCalls,

    /// <summary>The model or its provider refused the content.</summary>
    Refused,
}

/// <summary>What the turn used, as the engine reports it, why it stopped, and the model that answered.</summary>
public sealed record ChatUsage(int TokensIn, int TokensOut, ChatStop Stop, string Model) : IContract
{
    public bool IsValid() => TokensIn >= 0 && TokensOut >= 0 && Model.Length is > 0 and <= 200;
}
