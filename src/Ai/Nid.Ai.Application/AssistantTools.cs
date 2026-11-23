using System.Text.Json;
using Nid.Ai.Protocol.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Ai.Application;

/// <summary>
/// The tools the assistant may call (decision I, <c>docs/ai/README.md</c> §8.2, #499): a chosen set of Nid's MCP tools, each
/// with the data classes it returns (§6.1, §6.4), called in-process as the person (<see cref="Actor.Assistant"/>).
/// </summary>
public interface IAssistantTools
{
    /// <summary>The tools offered to the model, without <c>apply</c> or <c>documentId</c> (AST-11).</summary>
    IReadOnlyList<AssistantTool> Offered { get; }

    /// <summary>
    /// Calls <paramref name="tool"/> for <paramref name="person"/>, audited <c>via: assistant</c>; <paramref name="sharedOnly"/>
    /// for a turn that goes off the premises (<see cref="Actor.SharedOnly"/>). A tool not offered is refused; an answer longer
    /// than <paramref name="maxLength"/> characters is refused whole, never cut.
    /// </summary>
    Task<AssistantToolResult> CallAsync(PersonId person, bool sharedOnly, string tool, JsonElement arguments, int maxLength, CancellationToken ct);
}

/// <summary>A tool offered to the model and the data classes (<see cref="Nid.Ai.Contracts.DataClasses"/>) it returns.</summary>
public sealed record AssistantTool(ChatTool Schema, IReadOnlySet<string> DataClasses);

/// <summary>A tool's answer, as the model reads it: <see cref="Error"/> when the tool refused or failed.</summary>
public sealed record AssistantToolResult(bool Error, string Text);
