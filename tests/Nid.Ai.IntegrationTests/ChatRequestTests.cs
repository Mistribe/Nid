extern alias worker;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Protocol.Contracts;
using Nid.Ai.Worker.Tests;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using EngineOptions = worker::Nid.Ai.Worker.EngineOptions;
using EngineTransport = worker::Nid.Ai.Worker.EngineTransport;

namespace Nid.Ai.IntegrationTests;

/// <summary>#485: Nid streams a chat turn from a paired worker, whose engine is an OpenAI-compatible runtime behind it.</summary>
public sealed class ChatRequestTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_chat_turn_streams_from_the_engine_behind_a_paired_worker()
    {
        var engine = new FakeEngine();
        engine.Chunks.AddRange([FakeEngine.TextChunk("Bonjour "), FakeEngine.TextChunk("Alice."), FakeEngine.FinishChunk("stop"), FakeEngine.UsageChunk(12, 3)]);
        await using var world = await PairedWorld.StartAsync(fixture, workerServices: s =>
        {
            s.Replace(ServiceDescriptor.Singleton(new EngineTransport(engine)));
            s.Configure<EngineOptions>(o => o.Url = "http://engine:11434");
        });
        await world.PairAsync();

        var outputs = new List<ChatRequestOutput>();
        await using (var scope = world.Nid.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var input = new ChatRequestInput("llama3.2", [new ChatMessage(ChatRole.User, "Bonjour")], [], 128);
            await foreach (var frame in scope.ServiceProvider.GetRequiredService<IModelRequests>()
                               .StreamAsync("chat", 1, ContractJson.Write(input), "documents", null, TestContext.Current.CancellationToken))
                outputs.Add(ContractJson.Read<ChatRequestOutput>(frame.Span));
        }

        Assert.Equal("Bonjour Alice.", string.Concat(outputs.Select(o => o.Text)));
        Assert.Equal(new ChatUsage(12, 3, ChatStop.End, "llama3.2"), outputs[^1].Usage);

        // Its model is declared, so Nid says chat can run (decision D).
        var capabilities = await (await world.Nid.RawAsync(world.Nid.AdultB, HttpMethod.Get, "/api/v1/ai/capabilities")).Content
            .ReadFromJsonAsync<ModelCapabilities>(PairedWorld.NidJson, TestContext.Current.CancellationToken);
        Assert.Contains(capabilities!.Capabilities, c => c.Capability == "chat" && c.Local == 1);
    }
}
