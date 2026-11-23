using System.Runtime.CompilerServices;
using Nid.Ai.Contracts;
using Nid.Ai.Protocol.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Ai.Application;

/// <summary>
/// A chat turn asked of the gateway (#486, <c>docs/ingestion/llm-providers.md</c> §9): the use it serves, the conversation,
/// the tools, the most it may write and the class of the data it carries. The model is never the caller's: the use's.
/// </summary>
public sealed record LlmChatRequest(
    string Use,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ChatTool> Tools,
    int MaxTokens,
    string DataClass,
    Guid? ConversationId = null,
    Guid? JobId = null,
    double? Temperature = null,
    LlmDestination? Pin = null,
    Action<LlmDestination>? Served = null);

/// <summary>
/// Where a call went (#500): a household worker, local or remote, or a cloud provider. The assistant's first step fixes what
/// its turn may read (decision C, <c>docs/ai/README.md</c> §6.4); its later steps are pinned to that same destination
/// (<see cref="LlmChatRequest.Pin"/>), so what was read for one never reaches another. <see cref="LlmChatRequest.Served"/>
/// learns it once the call answers.
/// </summary>
/// <remarks><see cref="ContextTokens"/> (#527): how much the model that answered reads, as its worker declares it or its profile
/// says; null when unknown.</remarks>
public sealed record LlmDestination(LlmPlacement Placement, string Name, Guid? WorkerId = null, string? ProfileId = null, int? ContextTokens = null)
{
    /// <summary>Out of the household's premises: a remote worker or the cloud.</summary>
    public bool Offsite => Placement != LlmPlacement.Local;
}

public enum LlmPlacement
{
    Local,
    Remote,
    Cloud,
}

/// <summary>The one way to a model: every call is checked, routed along the household's chain and recorded.</summary>
public interface ILlmGateway
{
    /// <summary>The turn as it streams; a call that cannot go anywhere, or fails, ends on a failure with its <see cref="JobCodes"/> code.</summary>
    IAsyncEnumerable<Result<ChatRequestOutput>> ChatAsync(LlmChatRequest request, CancellationToken ct);
}

/// <summary>One link of the chain (§5.3): the household's workers first, then the enabled cloud profiles (#487).</summary>
public interface ILlmProvider
{
    /// <summary>Its place in the chain, lowest first.</summary>
    int Order { get; }

    /// <summary>Whether a use in <paramref name="mode"/> may reach this link at all.</summary>
    bool Allows(string mode);

    /// <summary>The turn; a failure first when this link has nowhere to send it, last when it failed on the way.</summary>
    IAsyncEnumerable<Result<ChatRequestOutput>> ChatAsync(ChatRequestInput input, string dataClass, LlmCallTrace trace, CancellationToken ct);
}

/// <summary>What a provider learns while it serves a call, for its <c>llm_call</c> row.</summary>
public sealed class LlmCallTrace
{
    public Guid? WorkerId { get; set; }

    /// <summary>#487: the cloud profile that served the call.</summary>
    public string? ProfileId { get; set; }

    public string? Provider { get; set; }

    public string? Tier { get; set; }

    /// <summary>#488: what the call held back on the budget, and what it was settled at.</summary>
    public decimal? ReservedEur { get; set; }

    public decimal? CostEur { get; set; }

    /// <summary>#500: the destination the call must go to, if pinned; the one that served it.</summary>
    public LlmDestination? Pin { get; init; }

    public LlmDestination? Destination { get; set; }
}

/// <summary>One model call as <c>llm_call</c> keeps it (§11): never its content.</summary>
public sealed record LlmCallRecord(
    Guid Id, Instant CreatedAt, Guid? PersonId, string Task, string Use, Guid? WorkerId, Guid? ConversationId, Guid? JobId, string? Model,
    int TokensIn, int TokensOut, string Status, string? Code, string? ProfileId = null, string? Provider = null, string? Tier = null,
    decimal? ReservedEur = null, decimal? CostEur = null);

public interface ILlmCalls
{
    Task RecordAsync(LlmCallRecord call, CancellationToken ct);
}

/// <summary>The statuses of <c>llm_call</c>.</summary>
public static class LlmCallStatus
{
    public const string Succeeded = "succeeded";
    public const string Refused = "refused";
    public const string Error = "error";
    public const string NoDestination = "no_destination";
    public const string BlockedBudget = "blocked_budget";
}

public sealed class LlmGateway(
    IAiSettings settings, IEnumerable<ILlmProvider> providers, ILlmCalls calls, IActorAccessor actors, IClock clock) : ILlmGateway
{
    /// <summary>A link that refuses with one of these had nowhere to send the call: the next link may.</summary>
    private static readonly HashSet<string> NoDestination =
        [JobCodes.NoWorker, JobCodes.AllOffline, JobCodes.RemoteNotAllowed, JobCodes.NoSafeWorker, JobCodes.ModelUnavailable,
            JobCodes.CloudNotAllowed, JobCodes.NoCloudProfile];

    public async IAsyncEnumerable<Result<ChatRequestOutput>> ChatAsync(LlmChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var started = clock.GetCurrentInstant();
        var trace = new LlmCallTrace { Pin = request.Pin };
        var outcome = new Outcome();
        try
        {
            var use = await settings.UseAsync(request.Use, ct);
            outcome.Model = use.Model;
            if (!Check(request, use).TryGet(out var input, out var refused))
            {
                outcome.Fail(refused!.Code);
                yield return Result.Failure<ChatRequestOutput>(refused);
                yield break;
            }

            var reason = JobCodes.NoWorker;
            foreach (var provider in providers.Where(p => p.Allows(use.Mode)).OrderBy(p => p.Order))
            {
                await using var outputs = provider.ChatAsync(input, request.DataClass, trace, ct).GetAsyncEnumerator(ct);
                var any = await outputs.MoveNextAsync();
                if (any && outputs.Current.TryGetFailure(out var first))
                {
                    if (NoDestination.Contains(first!.Code))
                    {
                        reason = first.Code; // nowhere here: the next link may take it
                        continue;
                    }

                    outcome.Fail(first.Code);
                    yield return outputs.Current;
                    yield break;
                }

                if (trace.Destination is { } served)
                    request.Served?.Invoke(served);
                ChatUsage? usage = null;
                if (trace.ProfileId is not null)
                    outcome.Model = null; // the profile's model: the usage frame says which
                while (any)
                {
                    if (!outputs.Current.TryGet(out var output, out var failed))
                    {
                        outcome.Fail(failed!.Code);
                        yield return outputs.Current;
                        yield break;
                    }

                    usage = output.Usage ?? usage;
                    yield return output;
                    any = await outputs.MoveNextAsync();
                }

                outcome.Model ??= usage?.Model;
                outcome.End(usage);
                yield break;
            }

            outcome.Fail(reason);
            yield return ModelFailures.Of(reason);
        }
        finally
        {
            // The caller may have gone: the row is written all the same.
            await calls.RecordAsync(new LlmCallRecord(Guid.CreateVersion7(), started, actors.Current?.PersonId?.Value, "chat", request.Use,
                trace.WorkerId, request.ConversationId, request.JobId, outcome.Model, outcome.TokensIn, outcome.TokensOut, outcome.Status,
                outcome.Code, trace.ProfileId, trace.Provider, trace.Tier, trace.ReservedEur, trace.CostEur), CancellationToken.None);
        }
    }

    /// <summary>What the gateway checks before anything leaves (§9): the use is on and may chat, a model is set, the turn holds.</summary>
    private static Result<ChatRequestInput> Check(LlmChatRequest request, UseSetting use)
    {
        if (!ModelUses.All.Contains(request.Use) || !ModelUses.TakesChat(request.Use))
            return ModelFailures.Of(JobCodes.TaskNotAllowed);
        if (request.Tools.Count > 0 && request.Use != ModelUses.Chat)
            return ModelFailures.Of(JobCodes.TaskNotAllowed); // tools only for the chat (§9)
        if (use.Mode == UseModes.Off)
            return ModelFailures.Of(JobCodes.UseOff);
        if (use.Model is not { } model)
            return ModelFailures.Of(JobCodes.NoModel);
        var input = new ChatRequestInput(model, request.Messages, request.Tools, request.MaxTokens, request.Temperature);
        return input.IsValid() ? input : ModelFailures.Of(JobCodes.InvalidInput);
    }

    /// <summary>How the call ended, for its row: an error with no code until it says otherwise (a caller that left).</summary>
    private sealed class Outcome
    {
        public string? Model { get; set; }

        public string Status { get; private set; } = LlmCallStatus.Error;

        public string? Code { get; private set; }

        public int TokensIn { get; private set; }

        public int TokensOut { get; private set; }

        public void Fail(string code)
            => (Status, Code) = (code == JobCodes.BudgetBlocked ? LlmCallStatus.BlockedBudget
                : code is JobCodes.UseOff or JobCodes.NoModel || NoDestination.Contains(code) ? LlmCallStatus.NoDestination
                : LlmCallStatus.Error, code);

        public void End(ChatUsage? usage)
        {
            (TokensIn, TokensOut) = (usage?.TokensIn ?? 0, usage?.TokensOut ?? 0);
            (Status, Code) = usage switch
            {
                null => (LlmCallStatus.Error, JobCodes.Cut), // a turn without its usage was cut
                { Stop: ChatStop.Refused } => (LlmCallStatus.Refused, null),
                _ => (LlmCallStatus.Succeeded, null),
            };
        }
    }
}

/// <summary>The household's workers (§5.3): local ones first, then remote ones, as the router orders them.</summary>
public sealed class WorkerProvider(IModelRequests requests) : ILlmProvider
{
    public int Order => 0;

    public bool Allows(string mode) => mode is UseModes.SelfHosted or UseModes.SelfHostedThenCloud;

    public async IAsyncEnumerable<Result<ChatRequestOutput>> ChatAsync(
        ChatRequestInput input, string dataClass, LlmCallTrace trace, [EnumeratorCancellation] CancellationToken ct)
    {
        if (trace.Pin is { Placement: LlmPlacement.Cloud })
        {
            yield return ModelFailures.Of(JobCodes.NoWorker); // pinned to a cloud provider: no worker may see this turn
            yield break;
        }

        await foreach (var frame in requests.StreamAsync("chat", 1, ContractJson.Write(input), dataClass, trace.Pin?.WorkerId, ct,
                           model: input.Model, served: worker => (trace.WorkerId, trace.Destination) = (worker.Id, new LlmDestination(
                               worker.Placement == WorkerPlacement.Remote ? LlmPlacement.Remote : LlmPlacement.Local, worker.Name, WorkerId: worker.Id,
                               ContextTokens: worker.ContextTokens))))
            yield return frame.TryGet(out var data, out var failed)
                ? ContractJson.Read<ChatRequestOutput>(data.Span)
                : Result.Failure<ChatRequestOutput>(failed!);
    }
}
