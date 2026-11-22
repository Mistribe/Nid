using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Ai.Protocol.Contracts;
using Nid.Ai.Protocol.Framing;

namespace Nid.Ai.Worker.Tests;

/// <summary>#485: the worker serves <c>chat</c> through the OpenAI-compatible engine behind it and declares its models.</summary>
public sealed class ChatTests
{
    private static readonly ChatRequestInput Hello = new(
        "llama3.2",
        [new ChatMessage(ChatRole.System, "Tu es l'assistant de Nid."), new ChatMessage(ChatRole.User, "Bonjour")],
        [new ChatTool("list_envelopes", "Les enveloppes du foyer.", JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement)],
        256);

    [Fact]
    public async Task Without_an_engine_it_declares_no_model_and_no_chat()
    {
        await using var worker = new WorkerHarness();
        using var nid = await PairedAsync(worker);

        var (_, declaration) = await nid.DeclarationAsync();

        Assert.Empty(declaration!.Models);
        Assert.DoesNotContain(declaration.Requests, r => r.Type == "chat");
    }

    [Fact]
    public async Task It_declares_the_engines_models_with_a_digest_and_limits()
    {
        var engine = new FakeEngine();
        await using var worker = Worker(engine);
        using var nid = await PairedAsync(worker);

        var (_, declaration) = await nid.DeclarationAsync();

        var model = Assert.Single(declaration!.Models);
        Assert.Equal(DeclaredCapability.Chat, model.Capability);
        Assert.Equal("llama3.2", model.Name);
        Assert.StartsWith("sha256:", model.Digest, StringComparison.Ordinal);
        Assert.Equal(8192, model.ContextTokens);
        Assert.Equal(4096, model.MaxOutputTokens);
        Assert.True(model.Tools);
        Assert.Contains(new ContractVersion("chat", 1), declaration.Requests);
        // The engine stays on the worker's network: its address never reaches Nid.
        Assert.DoesNotContain("engine:11434", System.Text.Encoding.UTF8.GetString(ContractJson.Write(declaration)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_pulled_again_shows_a_new_digest()
    {
        var engine = new FakeEngine();
        await using var worker = Worker(engine);
        using var nid = await PairedAsync(worker);
        var before = (await nid.DeclarationAsync()).Body!.Models[0].Digest;

        engine.Models[0]["created"] = 2;
        worker.Clock.Now += TimeSpan.FromMinutes(1); // past the cache of the engine's list

        Assert.NotEqual(before, (await nid.DeclarationAsync()).Body!.Models[0].Digest);
    }

    [Fact]
    public async Task Only_the_allowed_models_are_declared()
    {
        var engine = new FakeEngine();
        engine.Models.Add(new() { ["id"] = "qwen2.5:14b", ["object"] = "model" });
        await using var worker = Worker(engine, models: "qwen2.5:14b");
        using var nid = await PairedAsync(worker);

        Assert.Equal("qwen2.5:14b", Assert.Single((await nid.DeclarationAsync()).Body!.Models).Name);
    }

    [Fact]
    public async Task A_turn_streams_text_tool_calls_then_its_usage()
    {
        var engine = new FakeEngine();
        engine.Chunks.AddRange([
            FakeEngine.TextChunk("Je regarde "), FakeEngine.TextChunk("vos enveloppes."),
            FakeEngine.ToolChunk(0, "call_1", "list_envelopes", ""), FakeEngine.ToolChunk(0, null, null, "{}"),
            FakeEngine.FinishChunk("tool_calls"), FakeEngine.UsageChunk(42, 7),
        ]);
        await using var worker = Worker(engine);
        using var nid = await PairedAsync(worker);

        var (_, frames, _) = await nid.CallFramesAsync("/v1/requests/chat/1", ContractJson.Write(Hello), ct: TestContext.Current.CancellationToken);

        Assert.Equal(FrameType.End, frames[^1].Type);
        var outputs = frames.SkipLast(1).Select(f => ContractJson.Read<ChatRequestOutput>(f.Content.Span)).ToList();
        Assert.Equal("Je regarde vos enveloppes.", string.Concat(outputs.Select(o => o.Text)));
        var call = Assert.Single(outputs, o => o.ToolCall is not null).ToolCall!;
        Assert.Equal(new ChatToolCall("call_1", "list_envelopes", "{}"), call);
        Assert.Equal(new ChatUsage(42, 7, ChatStop.ToolCalls, "llama3.2"), outputs[^1].Usage);

        var sent = engine.ChatRequests.Single();
        Assert.Equal("llama3.2", sent["model"]!.GetValue<string>());
        Assert.True(sent["stream"]!.GetValue<bool>());
        Assert.Equal(256, sent["max_tokens"]!.GetValue<int>());
        Assert.Equal("system", sent["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("list_envelopes", sent["tools"]![0]!["function"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_cut_answer_says_it_stopped_at_its_length()
    {
        var engine = new FakeEngine();
        engine.Chunks.AddRange([FakeEngine.TextChunk("Bon"), FakeEngine.FinishChunk("length"), FakeEngine.UsageChunk(5, 256)]);
        await using var worker = Worker(engine);
        using var nid = await PairedAsync(worker);

        var (_, frames, _) = await nid.CallFramesAsync("/v1/requests/chat/1", ContractJson.Write(Hello), ct: TestContext.Current.CancellationToken);

        Assert.Equal(ChatStop.Length, ContractJson.Read<ChatRequestOutput>(frames[^2].Content.Span).Usage!.Stop);
    }

    [Theory]
    [InlineData(true, null, "engine_unreachable")]
    [InlineData(false, 404, "engine_refused")]
    [InlineData(false, 500, "engine_failed")]
    public async Task An_engine_that_is_down_or_refuses_ends_in_an_error_frame(bool down, int? status, string code)
    {
        var engine = new FakeEngine();
        await using var worker = Worker(engine);
        using var nid = await PairedAsync(worker);
        engine.Down = down;
        engine.Fails = (System.Net.HttpStatusCode?)status;

        var (_, frames, _) = await nid.CallFramesAsync("/v1/requests/chat/1", ContractJson.Write(Hello), ct: TestContext.Current.CancellationToken);

        Assert.Equal(FrameType.Error, frames[^1].Type);
        Assert.Equal(code, Encoding.UTF8.GetString(frames[^1].Content.Span));
    }

    [Fact]
    public async Task An_engine_that_is_down_leaves_the_last_known_models_out_of_the_declaration()
    {
        var engine = new FakeEngine { Down = true };
        await using var worker = Worker(engine);
        using var nid = await PairedAsync(worker);

        var (_, declaration) = await nid.DeclarationAsync();

        Assert.Empty(declaration!.Models);
    }

    internal static WorkerHarness Worker(FakeEngine engine, string? models = null)
        => new(services: s =>
        {
            s.Replace(ServiceDescriptor.Singleton(new EngineTransport(engine)));
            s.Configure<EngineOptions>(o =>
            {
                o.Url = "http://engine:11434";
                o.Models = models;
            });
        });

    private static async Task<TestNid> PairedAsync(WorkerHarness worker)
    {
        var nid = new TestNid(worker.CreateClient());
        await nid.FetchWorkerKeyAsync();
        await nid.PairAsync(worker.PairingCode());
        return nid;
    }
}
