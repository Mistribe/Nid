using System.Text.Json;
using UnambitiousFx.Engram;

namespace Nid.Platform.Tests;

public class CorpusTests
{
    private const string RegenerateVariable = "NID_REGENERATE_VECTORS";

    private sealed record Entry(string Input, string Canonical, string Sha256);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static List<Entry> Generate()
        => CorpusGenerator.Inputs().Select(input =>
        {
            using var doc = JsonDocument.Parse(input);
            return new Entry(input, JsonCanonicalizer.Canonicalize(doc.RootElement), JsonCanonicalizer.Hash(doc.RootElement));
        }).ToList();

    /// <summary>
    /// The committed file is the generator's output. Set <c>NID_REGENERATE_VECTORS=1</c> to rewrite it after
    /// a deliberate change to the generator — never to make a canonicalizer change pass.
    /// </summary>
    [Fact]
    public void The_committed_corpus_is_what_the_generator_produces()
    {
        var generated = Generate();

        if (Environment.GetEnvironmentVariable(RegenerateVariable) == "1")
        {
            File.WriteAllText(Vectors.InRepository("corpus.json"), JsonSerializer.Serialize(generated, Json) + "\n");
            return;
        }

        var committed = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(Vectors.Copied("corpus.json")), Json)!;
        Assert.Equal(CorpusGenerator.RandomDocuments + CorpusGenerator.Adversarial.Length, committed.Count);
        Assert.Equal(generated, committed);
    }

    [Fact]
    public void Canonicalizing_a_canonical_form_changes_nothing()
    {
        foreach (var entry in Generate())
        {
            using var doc = JsonDocument.Parse(entry.Canonical);
            Assert.Equal(entry.Canonical, JsonCanonicalizer.Canonicalize(doc.RootElement));
        }
    }

    [Fact]
    public void The_generator_exercises_normalization()
    {
        var entries = Generate();
        Assert.True(entries.Count(e => e.Input != e.Canonical) > entries.Count / 2,
            "Most inputs must differ from their canonical form, or the corpus proves nothing.");
    }
}
