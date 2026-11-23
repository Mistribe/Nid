using System.Runtime.CompilerServices;
using Nid.Ai.Application;
using Nid.Ai.Protocol.Contracts;
using UnambitiousFx.Functional;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>A model that answers from a script, step by step (the last step repeats), served at one destination.</summary>
internal sealed class ScriptedGateway(LlmDestination destination, params Func<LlmChatRequest, ChatRequestOutput[]>[] steps) : ILlmGateway
{
    public List<LlmChatRequest> Requests { get; } = [];

    public TimeSpan Delay { get; init; }

    /// <summary>The code every call is refused with, before any output.</summary>
    public string? Refuses { get; init; }

    public async IAsyncEnumerable<Result<ChatRequestOutput>> ChatAsync(LlmChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var step = steps[Math.Min(Requests.Count, steps.Length - 1)];
        Requests.Add(request with { Messages = [.. request.Messages] });
        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, ct);
        if (Refuses is not null)
        {
            yield return ModelFailures.Of(Refuses);
            yield break;
        }
        var outputs = step(request);
        request.Served?.Invoke(destination);
        foreach (var output in outputs)
            yield return output;
    }
}
