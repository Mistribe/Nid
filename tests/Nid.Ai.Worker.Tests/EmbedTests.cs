using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Ai.Protocol.Contracts;
using Nid.Ai.Protocol.Framing;

namespace Nid.Ai.Worker.Tests;

/// <summary>#558: the worker embeds through its engine's <c>/v1/embeddings</c>, for the models it is told serve <c>embed</c>.</summary>
public sealed class EmbedTests
{
    [Fact]
    public async Task It_declares_the_embed_models_it_is_told_with_their_dimension_and_the_others_as_chat()
    {
        await using var worker = Worker(Engine(), embedModels: "nomic-embed-text=4");
        using var nid = await PairedAsync(worker);

        var declaration = (await nid.DeclarationAsync()).Body!;

        var embed = Assert.Single(declaration.Models, m => m.Capability == DeclaredCapability.Embed);
        Assert.Equal(("nomic-embed-text", 4), (embed.Name, embed.Dimension));
        Assert.Equal("llama3.2", Assert.Single(declaration.Models, m => m.Capability == DeclaredCapability.Chat).Name);
        Assert.Contains(new ContractVersion("embed", 1), declaration.Requests);
    }

    [Fact]
    public async Task Without_an_embed_model_it_declares_no_embed()
    {
        await using var worker = Worker(Engine(), embedModels: null);
        using var nid = await PairedAsync(worker);

        Assert.DoesNotContain((await nid.DeclarationAsync()).Body!.Requests, r => r.Type == "embed");
    }

    [Fact]
    public async Task A_batch_comes_back_one_vector_per_text_in_its_order()
    {
        var engine = Engine();
        await using var worker = Worker(engine, embedModels: "nomic-embed-text=4");
        using var nid = await PairedAsync(worker);

        var (_, frames, _) = await nid.CallFramesAsync("/v1/requests/embed/1",
            ContractJson.Write(new EmbedRequestInput("nomic-embed-text", ["Bail", "Quittance de loyer"])), ct: TestContext.Current.CancellationToken);

        Assert.Equal(FrameType.End, frames[^1].Type);
        var outputs = frames.SkipLast(1).Select(f => ContractJson.Read<EmbedRequestOutput>(f.Content.Span)).ToList();
        Assert.Equal([0, 1], outputs.Select(o => o.Index));
        Assert.Equal([4f, 18f], outputs.Select(o => o.Vector[0])); // the fake engine's first value: the text's length
        Assert.All(outputs, o => Assert.Equal(4, o.Vector.Count));
        var sent = engine.EmbedRequests.Single();
        Assert.Equal("nomic-embed-text", sent["model"]!.GetValue<string>());
        Assert.Equal(["Bail", "Quittance de loyer"], sent["input"]!.AsArray().Select(t => t!.GetValue<string>()));
    }

    [Fact]
    public async Task Vectors_of_another_dimension_than_declared_end_in_an_error()
    {
        var engine = Engine();
        engine.EmbedDimension = 3;
        await using var worker = Worker(engine, embedModels: "nomic-embed-text=4");
        using var nid = await PairedAsync(worker);

        var (_, frames, _) = await nid.CallFramesAsync("/v1/requests/embed/1",
            ContractJson.Write(new EmbedRequestInput("nomic-embed-text", ["Bail"])), ct: TestContext.Current.CancellationToken);

        Assert.Equal(FrameType.Error, frames[^1].Type);
        Assert.Equal("engine_invalid", Encoding.UTF8.GetString(frames[^1].Content.Span));
    }

    private static FakeEngine Engine()
    {
        var engine = new FakeEngine();
        engine.Models.Add(new JsonObject { ["id"] = "nomic-embed-text", ["object"] = "model" });
        return engine;
    }

    private static WorkerHarness Worker(FakeEngine engine, string? embedModels)
        => new(services: s =>
        {
            s.Replace(ServiceDescriptor.Singleton(new EngineTransport(engine)));
            s.Configure<EngineOptions>(o =>
            {
                o.Url = "http://engine:11434";
                o.EmbedModels = embedModels;
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
