using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using Nid.Ai.Protocol.Contracts;
using Nid.Ai.Protocol.Framing;

namespace Nid.Ai.Worker.Tests;

/// <summary>#811: the worker serves <c>decide</c> with an NLI cross-encoder of its own, through ONNX Runtime. <c>Fixtures/nli</c> stands in
/// for the model (<c>make.py</c>): its entailment logit is the number of tokens in the pair, so every score can be counted here.</summary>
public sealed class NliDecideTests
{
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "nli");

    private static readonly IReadOnlyList<DecideOption> Options =
        [new("A", "Alimentation › Courses"), new("B", "Logement › Loyer et charges de copropriété"), new("C", "Aucune de ces catégories")];

    [Fact]
    public async Task It_declares_the_mounted_model_for_decide_with_the_digest_of_its_weights_and_no_engine()
    {
        await using var worker = Worker(Fixture, name: null);
        using var nid = await PairedAsync(worker);

        var declaration = (await nid.DeclarationAsync()).Body!;

        var model = Assert.Single(declaration.Models);
        Assert.Equal((DeclaredCapability.Decide, "nli", new DecideImplementation("nli-onnx", "1", DecideScores.Native)), (model.Capability, model.Name, model.Decide));
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(Fixture, "model.onnx"),
            TestContext.Current.CancellationToken))), model.Digest);
        Assert.Contains(new ContractVersion("decide", 1), declaration.Requests);
        Assert.DoesNotContain(declaration.Requests, r => r.Type == "chat");
    }

    [Fact]
    public async Task Each_option_scores_the_entailment_logit_of_its_pair_and_the_best_is_the_choice()
    {
        await using var worker = Worker(Fixture, name: "mdeberta-xnli");
        using var nid = await PairedAsync(worker);

        var frames = await CallAsync(nid, new DecideRequestInput("mdeberta-xnli", Options, ["CB LIDL", "Sens : débit\nMotif : PRLV LOYER"]));

        Assert.Equal(FrameType.End, frames[^1].Type);
        var outputs = frames.SkipLast(1).Select(f => ContractJson.Read<DecideRequestOutput>(f.Content.Span)).ToList();
        Assert.Equal([0, 1], outputs.Select(o => o.Index));
        using var spm = File.OpenRead(Path.Combine(Fixture, "spm.model"));
        var tokenizer = SentencePieceTokenizer.Create(spm, false, false);
        foreach (var (output, text) in outputs.Zip(["CB LIDL", "Sens : débit\nMotif : PRLV LOYER"]))
        {
            // [CLS] text [SEP] hypothesis [SEP]: the stand-in's entailment (index 2 of its config) counts them, padding left out.
            var expected = Options.Select(o => (o.Id, Score: (double)(3 + tokenizer.CountTokens(text, false, false)
                                                                         + tokenizer.CountTokens(NliDecider.HypothesisOf(o.Name), false, false)))).ToList();
            Assert.Equal(expected, output.Scores.Select(s => (s.Id, s.Score)));
            Assert.Equal(expected.MaxBy(e => e.Score).Id, output.Choice);
            Assert.Equal(("nli-onnx", "1"), (output.Implementation, output.ImplementationVersion));
        }
    }

    [Fact]
    public async Task Beside_an_engine_each_model_answers_its_own_requests()
    {
        var engine = new FakeEngine();
        engine.Models.Add(new System.Text.Json.Nodes.JsonObject { ["id"] = "qwen2.5:3b", ["object"] = "model" });
        await using var worker = Worker(Fixture, name: null, engine);
        using var nid = await PairedAsync(worker);

        var decide = (await nid.DeclarationAsync()).Body!.Models.Where(m => m.Capability == DeclaredCapability.Decide).ToList();
        Assert.Equal([("nli", "nli-onnx"), ("qwen2.5:3b", "llm-choice")], decide.Select(m => (m.Name, m.Decide!.Name)).Order());

        var llm = await CallAsync(nid, new DecideRequestInput("qwen2.5:3b", Options, ["CB LIDL"]));
        Assert.Equal("llm-choice", ContractJson.Read<DecideRequestOutput>(llm[0].Content.Span).Implementation);
        var nli = await CallAsync(nid, new DecideRequestInput("nli", Options, ["CB LIDL"]));
        Assert.Equal("nli-onnx", ContractJson.Read<DecideRequestOutput>(nli[0].Content.Span).Implementation);
        Assert.Single(engine.ChatRequests);
    }

    [Fact]
    public async Task A_directory_that_does_not_load_declares_nothing_and_the_worker_still_answers()
    {
        var empty = Directory.CreateTempSubdirectory("nid-nli-").FullName;
        await File.WriteAllTextAsync(Path.Combine(empty, "model.onnx"), "not a model", TestContext.Current.CancellationToken);
        foreach (var directory in new[] { Path.Combine(empty, "missing"), empty })
        {
            await using var worker = Worker(directory, name: null);
            using var nid = await PairedAsync(worker);

            var declaration = (await nid.DeclarationAsync()).Body!;
            Assert.Empty(declaration.Models);
            Assert.DoesNotContain(declaration.Requests, r => r.Type == "decide");
            var frames = await CallAsync(nid, new DecideRequestInput("missing", Options, ["CB LIDL"]));
            Assert.Equal("unsupported", Encoding.UTF8.GetString(frames[^1].Content.Span));
        }
    }

    /// <summary>The real checkpoint, when <c>NID_NLI_MODEL</c> names its exported directory (<c>docs/ingestion/llm-providers.md</c> §14.1).</summary>
    [Fact]
    public void The_real_checkpoint_puts_a_rent_in_housing()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NID_NLI_MODEL") is { Length: > 0 }, "NID_NLI_MODEL names no exported model.");
        using var nli = new NliDecider(Microsoft.Extensions.Options.Options.Create(new EngineOptions { DecideOnnx = Environment.GetEnvironmentVariable("NID_NLI_MODEL") }),
            NullLogger<NliDecider>.Instance);

        var (choice, scores) = nli.Decide([new("A", "Loisirs › Sport"), new("B", "Logement › Loyer")], "Sens : débit\nMotif : LOYER APPARTEMENT\nLibellé : PRLV SEPA LOYER APPARTEMENT");

        Assert.Equal("B", choice);
        Assert.Equal(2, scores.Count);
    }

    private static async Task<IReadOnlyList<Frame>> CallAsync(TestNid nid, DecideRequestInput input)
        => (await nid.CallFramesAsync("/v1/requests/decide/1", ContractJson.Write(input), ct: TestContext.Current.CancellationToken)).Frames;

    private static WorkerHarness Worker(string directory, string? name, FakeEngine? engine = null)
        => new(services: s =>
        {
            if (engine is not null)
                s.Replace(ServiceDescriptor.Singleton(new EngineTransport(engine)));
            s.Configure<EngineOptions>(o =>
            {
                o.Url = engine is null ? null : "http://engine:11434";
                o.DecideModels = engine is null ? null : "qwen2.5:3b";
                o.Models = engine is null ? null : "none";
                o.DecideOnnx = directory;
                o.DecideOnnxName = name;
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
