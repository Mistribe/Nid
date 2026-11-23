using System.Runtime.CompilerServices;
using Nid.Ai.Application;
using Nid.Ai.Protocol.Contracts;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>A model that answers from a script, step by step (the last step repeats), served at one destination.</summary>
internal sealed class ScriptedGateway(LlmDestination destination, params Func<LlmChatRequest, ChatRequestOutput[]>[] steps) : ILlmGateway
{
    public List<LlmChatRequest> Requests { get; } = [];

    public TimeSpan Delay { get; init; }

    public async IAsyncEnumerable<ChatRequestOutput> ChatAsync(LlmChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var step = steps[Math.Min(Requests.Count, steps.Length - 1)];
        Requests.Add(request with { Messages = [.. request.Messages] });
        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, ct);
        var outputs = step(request);
        request.Served?.Invoke(destination);
        foreach (var output in outputs)
            yield return output;
    }
}
