using System.Runtime.CompilerServices;
using Nid.Ai.Contracts;
using Nid.Ai.Protocol.Contracts;
using Nid.Platform.Contracts;
using NodaTime;

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
    double? Temperature = null);

/// <summary>The one way to a model: every call is checked, routed along the household's chain and recorded.</summary>
public interface ILlmGateway
{
    /// <summary>The turn as it streams; a call that cannot go anywhere throws <see cref="ModelCallException"/> with its reason.</summary>
    IAsyncEnumerable<ChatRequestOutput> ChatAsync(LlmChatRequest request, CancellationToken ct);
}

/// <summary>One link of the chain (§5.3): the household's workers first, then the enabled cloud profiles (#487).</summary>
public interface ILlmProvider
{
    /// <summary>Its place in the chain, lowest first.</summary>
    int Order { get; }

    /// <summary>Whether a use in <paramref name="mode"/> may reach this link at all.</summary>
    bool Allows(string mode);

    /// <summary>The turn; <see cref="ModelCallException"/> before any output when this link has nowhere to send it.</summary>
    IAsyncEnumerable<ChatRequestOutput> ChatAsync(ChatRequestInput input, string dataClass, LlmCallTrace trace, CancellationToken ct);
}

/// <summary>What a provider learns while it serves a call, for its <c>llm_call</c> row.</summary>
public sealed class LlmCallTrace
{
    public Guid? WorkerId { get; set; }
}

/// <summary>One model call as <c>llm_call</c> keeps it (§11): never its content.</summary>
public sealed record LlmCallRecord(
    Guid Id, Instant CreatedAt, Guid? PersonId, string Task, string Use, Guid? WorkerId, Guid? ConversationId, Guid? JobId, string? Model,
    int TokensIn, int TokensOut, string Status, string? Code);

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
        [JobCodes.NoWorker, JobCodes.AllOffline, JobCodes.RemoteNotAllowed, JobCodes.NoSafeWorker, JobCodes.ModelUnavailable];

    public async IAsyncEnumerable<ChatRequestOutput> ChatAsync(LlmChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var started = clock.GetCurrentInstant();
        var trace = new LlmCallTrace();
        var outcome = new Outcome();
        try
        {
            ChatRequestInput input;
            List<ILlmProvider> chain;
            try
            {
                var use = await settings.UseAsync(request.Use, ct);
                outcome.Model = use.Model;
                input = Check(request, use);
                chain = [.. providers.Where(p => p.Allows(use.Mode)).OrderBy(p => p.Order)];
            }
            catch (ModelCallException e)
            {
                outcome.Fail(e.Code);
                throw;
            }

            var reason = JobCodes.NoWorker;
            foreach (var provider in chain)
            {
                await using var outputs = provider.ChatAsync(input, request.DataClass, trace, ct).GetAsyncEnumerator(ct);
                bool any;
                try
                {
                    any = await outputs.MoveNextAsync();
                }
                catch (ModelCallException e) when (NoDestination.Contains(e.Code))
                {
                    reason = e.Code; // nowhere here: the next link may take it
                    continue;
                }
                catch (ModelCallException e)
                {
                    outcome.Fail(e.Code);
                    throw;
                }

                ChatUsage? usage = null;
                while (any)
                {
                    usage = outputs.Current.Usage ?? usage;
                    yield return outputs.Current;
                    any = await NextAsync(outputs, outcome);
                }

                outcome.End(usage);
                yield break;
            }

            outcome.Fail(reason);
            throw new ModelCallException(reason);
        }
        finally
        {
            // The caller may have gone: the row is written all the same.
            await calls.RecordAsync(new LlmCallRecord(Guid.CreateVersion7(), started, actors.Current?.PersonId?.Value, "chat", request.Use,
                trace.WorkerId, request.ConversationId, request.JobId, outcome.Model, outcome.TokensIn, outcome.TokensOut, outcome.Status,
                outcome.Code), CancellationToken.None);
        }
    }

    private static async Task<bool> NextAsync(IAsyncEnumerator<ChatRequestOutput> outputs, Outcome outcome)
    {
        try
        {
            return await outputs.MoveNextAsync();
        }
        catch (ModelCallException e)
        {
            outcome.Fail(e.Code);
            throw;
        }
    }

    /// <summary>What the gateway checks before anything leaves (§9): the use is on and may chat, a model is set, the turn holds.</summary>
    private static ChatRequestInput Check(LlmChatRequest request, UseSetting use)
    {
        if (!ModelUses.All.Contains(request.Use) || !ModelUses.TakesChat(request.Use))
            throw new ModelCallException(JobCodes.TaskNotAllowed);
        if (request.Tools.Count > 0 && request.Use != ModelUses.Chat)
            throw new ModelCallException(JobCodes.TaskNotAllowed); // tools only for the chat (§9)
        if (use.Mode == UseModes.Off)
            throw new ModelCallException(JobCodes.UseOff);
        if (use.Model is not { } model)
            throw new ModelCallException(JobCodes.NoModel);
        var input = new ChatRequestInput(model, request.Messages, request.Tools, request.MaxTokens, request.Temperature);
        return input.IsValid() ? input : throw new ModelCallException(JobCodes.InvalidInput);
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
            => (Status, Code) = (code is JobCodes.UseOff or JobCodes.NoModel || NoDestination.Contains(code) ? LlmCallStatus.NoDestination : LlmCallStatus.Error, code);

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

    public async IAsyncEnumerable<ChatRequestOutput> ChatAsync(
        ChatRequestInput input, string dataClass, LlmCallTrace trace, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var frame in requests.StreamAsync("chat", 1, ContractJson.Write(input), dataClass, null, ct,
                           model: input.Model, served: worker => trace.WorkerId = worker))
            yield return ContractJson.Read<ChatRequestOutput>(frame.Span);
    }
}
