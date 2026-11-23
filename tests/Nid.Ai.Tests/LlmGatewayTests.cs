using System.Runtime.CompilerServices;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Protocol.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Ai.Tests;

/// <summary>#509: a turn the gateway refuses, or a link that fails, ends the stream on its code and is recorded so.</summary>
public sealed class LlmGatewayTests
{
    private static readonly IReadOnlyList<ChatMessage> Hello = [new(ChatRole.User, "Bonjour")];

    [Fact]
    public async Task A_use_that_may_not_chat_is_refused_before_any_link()
    {
        var link = new ScriptedLink(0, Text("jamais"));
        var calls = new Calls();

        var (outputs, code) = await ChatAsync(new LlmChatRequest(ModelUses.Embed, Hello, [], 64, DataClasses.Figures), calls, link);

        Assert.Empty(outputs);
        Assert.Equal(JobCodes.TaskNotAllowed, code);
        Assert.Equal(0, link.Calls);
        Assert.Equal((LlmCallStatus.Error, JobCodes.TaskNotAllowed), (Assert.Single(calls.Recorded).Status, calls.Recorded[0].Code));
    }

    [Fact]
    public async Task A_link_that_fails_after_answering_ends_the_turn_there()
    {
        var first = new ScriptedLink(0, Text("Bon"), ModelFailures.Of(JobCodes.CloudFailed));
        var second = new ScriptedLink(1, Text("jamais"));
        var calls = new Calls();

        var (outputs, code) = await ChatAsync(new LlmChatRequest(ModelUses.Chat, Hello, [], 64, DataClasses.Figures), calls, first, second);

        Assert.Equal("Bon", Assert.Single(outputs).Text);
        Assert.Equal(JobCodes.CloudFailed, code);
        Assert.Equal(0, second.Calls);
        Assert.Equal((LlmCallStatus.Error, JobCodes.CloudFailed), (Assert.Single(calls.Recorded).Status, calls.Recorded[0].Code));
    }

    [Fact]
    public async Task A_link_that_refuses_outright_is_not_handed_over()
    {
        var first = new ScriptedLink(0, ModelFailures.Of(JobCodes.CloudRefused));
        var second = new ScriptedLink(1, Text("jamais"));

        var (outputs, code) = await ChatAsync(new LlmChatRequest(ModelUses.Chat, Hello, [], 64, DataClasses.Figures), new Calls(), first, second);

        Assert.Empty(outputs);
        Assert.Equal(JobCodes.CloudRefused, code);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task A_turn_pinned_to_the_cloud_never_reaches_a_worker()
    {
        var requests = new NoRequests();
        var items = new List<Result<ChatRequestOutput>>();

        await foreach (var item in new WorkerProvider(requests).ChatAsync(new ChatRequestInput("llama3.2", Hello, [], 64), DataClasses.Figures,
                           new LlmCallTrace { Pin = LlmClasses.Cloud + "anthropic-claude" }, TestContext.Current.CancellationToken))
            items.Add(item);

        Assert.True(Assert.Single(items).TryGetFailure(out var failure));
        Assert.Equal(JobCodes.NoWorker, failure!.Code);
        Assert.Equal(0, requests.Calls);
    }

    private static Result<ChatRequestOutput> Text(string text) => Result.Success(new ChatRequestOutput(Text: text));

    /// <summary>The whole turn: its outputs, and the failure's code, which must be its last item.</summary>
    private static async Task<(List<ChatRequestOutput> Outputs, string? Code)> ChatAsync(LlmChatRequest request, Calls calls, params ILlmProvider[] links)
    {
        var gateway = new LlmGateway(new Settings(), links, calls, new ActorAccessor(), SystemClock.Instance);
        var items = new List<Result<ChatRequestOutput>>();
        await foreach (var item in gateway.ChatAsync(request, TestContext.Current.CancellationToken))
            items.Add(item);

        Assert.All(items.SkipLast(1), i => Assert.True(i.IsSuccess));
        var outputs = items.Select(i => i.TryGetValue(out var o) ? o : null).OfType<ChatRequestOutput>().ToList();
        return (outputs, items is [.., var last] && last.TryGetFailure(out var failure) ? failure!.Code : null);
    }

    /// <summary>A link of the chain at <paramref name="order"/> that answers its script.</summary>
    private sealed class ScriptedLink(int order, params Result<ChatRequestOutput>[] script) : ILlmProvider
    {
        public int Calls { get; private set; }

        public int Order => order;

        public bool Allows(string mode) => true;

        public async IAsyncEnumerable<Result<ChatRequestOutput>> ChatAsync(
            ChatRequestInput input, string dataClass, LlmCallTrace trace, [EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            foreach (var item in script)
            {
                await Task.Yield();
                yield return item;
            }
        }
    }

    private sealed class NoRequests : IModelRequests
    {
        public int Calls { get; private set; }

        public IAsyncEnumerable<Result<ReadOnlyMemory<byte>>> StreamAsync(string type, int version, byte[] input, string dataClass, Guid? target,
            CancellationToken ct, string? model = null, Action<ServingWorker>? served = null, Func<ServingWorker, bool>? only = null)
        {
            Calls++;
            return AsyncEnumerable.Empty<Result<ReadOnlyMemory<byte>>>();
        }
    }

    private sealed class Calls : ILlmCalls
    {
        public List<LlmCallRecord> Recorded { get; } = [];

        public Task RecordAsync(LlmCallRecord call, CancellationToken ct)
        {
            Recorded.Add(call);
            return Task.CompletedTask;
        }
    }

    /// <summary>Every use on its workers then the cloud, with a model.</summary>
    private sealed class Settings : IAiSettings
    {
        public Task<bool> RemoteWorkersAllowedAsync(CancellationToken ct) => Task.FromResult(false);

        public Task<IReadOnlyDictionary<string, UseSetting>> UsesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, UseSetting>>(
                ModelUses.All.ToDictionary(u => u, _ => new UseSetting(UseModes.SelfHostedThenCloud, "llama3.2")));

        public Task<bool> SetUseModeAsync(string use, string mode, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> SetUseModelAsync(string use, string? model, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> SetRemoteWorkersAllowedAsync(bool allowed, Guid person, Instant at, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> ConversationRetentionDaysAsync(CancellationToken ct) => Task.FromResult(30);

        public Task SetConversationRetentionDaysAsync(int days, CancellationToken ct) => throw new NotSupportedException();
    }
}
