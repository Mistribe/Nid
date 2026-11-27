using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;
using Nid.Ai.Protocol.Contracts;

namespace Nid.Ai.Worker;

/// <summary>
/// #811: a zero-shot NLI cross-encoder (an mDeBERTa-v3 XNLI checkpoint exported to ONNX) the worker runs itself, through ONNX Runtime on
/// the CPU, from the directory <see cref="EngineOptions.DecideOnnx"/> mounts: <c>model.onnx</c>, the checkpoint's SentencePiece
/// <c>spm.model</c> and its <c>config.json</c> (which logit is entailment). Nothing is downloaded. A directory that does not load is logged
/// and nothing is declared.
/// </summary>
public sealed partial class NliDecider : IDisposable
{
    /// <summary>The version changes with the hypothesis (<see cref="HypothesisOf"/>) and the score: what a calibration measured holds for those.</summary>
    public static readonly DecideImplementation Implementation = new("nli-onnx", "1", DecideScores.Native);

    /// <summary>The checkpoint reads 512 tokens at most; the text is cut, never the hypothesis.</summary>
    private const int MaxTokens = 512;

    private readonly Lazy<Loaded?> _loaded;

    public NliDecider(IOptions<EngineOptions> options, ILogger<NliDecider> logger)
    {
        var o = options.Value;
        _loaded = new(() =>
        {
            if (o.DecideOnnx is not { Length: > 0 } directory)
                return null;
            try
            {
                return Load(directory, o.DecideOnnxName is { Length: > 0 } name ? name : Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OnnxRuntimeException or JsonException or InvalidDataException
                                          or ArgumentException or KeyNotFoundException or InvalidOperationException)
            {
                LogLoadFailed(logger, directory, e.GetType().Name, e.Message);
                return null;
            }
        });
    }

    /// <summary>The model it serves for <c>decide</c>; null when none is set or it does not load.</summary>
    public DeclaredModel? Model => _loaded.Value?.Declared;

    /// <summary>One forward pass per (text, option) pair, the text as premise and the option as hypothesis; an option's score is the
    /// pair's entailment logit (<see cref="DecideScores.Native"/>), the choice the option scored best.</summary>
    public (string? Choice, IReadOnlyList<DecideScore> Scores) Decide(IReadOnlyList<DecideOption> options, string text)
    {
        var model = _loaded.Value ?? throw new EngineException("engine_unconfigured");
        var premise = model.Tokenizer.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false);
        var pairs = options.Select(o => Pair(model, premise, model.Tokenizer.EncodeToIds(HypothesisOf(o.Name), false, false))).ToList();
        var length = pairs.Max(p => p.Count);
        var ids = new long[pairs.Count * length];
        var mask = new long[pairs.Count * length];
        for (var i = 0; i < pairs.Count; i++)
            for (var j = 0; j < pairs[i].Count; j++)
                (ids[i * length + j], mask[i * length + j]) = (pairs[i][j], 1);

        long[] shape = [pairs.Count, length];
        using var inputIds = OrtValue.CreateTensorValueFromMemory(ids, shape);
        using var attention = OrtValue.CreateTensorValueFromMemory(mask, shape);
        using var types = OrtValue.CreateTensorValueFromMemory(new long[ids.Length], shape);
        var inputs = new Dictionary<string, OrtValue> { ["input_ids"] = inputIds, ["attention_mask"] = attention };
        if (model.Session.InputMetadata.ContainsKey("token_type_ids"))
            inputs["token_type_ids"] = types;
        using var run = new RunOptions();
        using var outputs = model.Session.Run(run, inputs, [model.Session.OutputNames[0]]);
        var logits = outputs[0].GetTensorDataAsSpan<float>().ToArray();
        var labels = logits.Length / pairs.Count;
        IReadOnlyList<DecideScore> scores = [.. options.Select((o, i) => new DecideScore(o.Id, logits[i * labels + model.Entailment]))
            .Where(s => double.IsFinite(s.Score))];
        return (scores.Count == 0 ? null : scores.MaxBy(s => s.Score)!.Id, scores);
    }

    /// <summary>What an option reads as, « none of these » included like any other (a hypothesis of its own measured no better,
    /// <c>docs/ingestion/llm-providers.md</c> §14.1).</summary>
    public static string HypothesisOf(string option) => $"Cette opération bancaire concerne : {option}.";

    /// <summary>[CLS] text [SEP] hypothesis [SEP], the text cut so the pair fits.</summary>
    private static List<int> Pair(Loaded model, IReadOnlyList<int> premise, IReadOnlyList<int> hypothesis)
    {
        var room = Math.Max(0, MaxTokens - 3 - hypothesis.Count);
        return [model.Tokenizer.BeginningOfSentenceId, .. premise.Take(room), model.Tokenizer.EndOfSentenceId, .. hypothesis.Take(MaxTokens - 3),
            model.Tokenizer.EndOfSentenceId];
    }

    private static Loaded Load(string directory, string name)
    {
        var onnx = Path.Combine(directory, "model.onnx");
        string digest;
        using (var file = File.OpenRead(onnx))
            digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(file));
        using var config = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "config.json")));
        var labels = config.RootElement.GetProperty("id2label").EnumerateObject()
            .ToDictionary(p => p.Value.GetString()!.ToLowerInvariant(), p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture));
        SentencePieceTokenizer tokenizer;
        using (var spm = File.OpenRead(Path.Combine(directory, "spm.model")))
            tokenizer = SentencePieceTokenizer.Create(spm, false, false);
        var session = new InferenceSession(onnx);
        return new Loaded(session, tokenizer, labels["entailment"],
            new DeclaredModel(DeclaredCapability.Decide, name, digest, MaxTokens, 1, Decide: Implementation));
    }

    public void Dispose()
    {
        if (_loaded.IsValueCreated)
            _loaded.Value?.Session.Dispose();
    }

    private sealed record Loaded(InferenceSession Session, SentencePieceTokenizer Tokenizer, int Entailment, DeclaredModel Declared);

    [LoggerMessage(Level = LogLevel.Error, Message = "The decide model in {Directory} could not be loaded ({Error}: {Detail}); it is not served")]
    private static partial void LogLoadFailed(ILogger logger, string directory, string error, string detail);
}
