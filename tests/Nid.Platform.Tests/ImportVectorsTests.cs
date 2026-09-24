using System.Text.Json;
using Nid.Platform.Canonical;

namespace Nid.Platform.Tests;

/// <summary>
/// The batch hashes of the shared nid.data fixtures, as the server computes them. The browser cuts the same files
/// into batches and must find the same hashes (web design §6.3); both sides read <c>tests/vectors/import/</c>.
/// </summary>
public class ImportVectorsTests
{
    private const string RegenerateVariable = "NID_REGENERATE_VECTORS";
    private const int BatchSize = 500; // import-format.md §11.2
    private static readonly string[] Files = ["batching.nid.json", "worked-examples.nid.json"];

    private sealed record Batch(string File, string Section, int Index, int Count, string Sha256);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>1 201 entities in one section: two full batches and a partial one. Numbers carry a trailing zero.</summary>
    internal static string BatchingFile()
    {
        var entities = Enumerable.Range(0, 1201)
            .Select(i => $$"""    {"id":"s-{{i:D4}}","name":"Abonnement {{i}} — ç","rank":{{i}}.50}""");
        return "{\n  \"format\": \"nid.data\",\n  \"version\": 1,\n  \"subscriptions\": [\n"
            + string.Join(",\n", entities) + "\n  ]\n}\n";
    }

    private static List<Batch> BatchesOf(string file, string text)
    {
        using var document = JsonDocument.Parse(text);
        var batches = new List<Batch>();
        foreach (var section in document.RootElement.EnumerateObject()
                     .Where(p => p.Value.ValueKind == JsonValueKind.Array)
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var entities = section.Value.EnumerateArray().ToList();
            for (var index = 0; index * BatchSize < entities.Count; index++)
            {
                var slice = entities.Skip(index * BatchSize).Take(BatchSize).ToList();
                using var batch = JsonDocument.Parse("[" + string.Join(",", slice.Select(e => e.GetRawText())) + "]");
                batches.Add(new Batch(file, section.Name, index, slice.Count, Rfc8785.Hash(batch.RootElement)));
            }
        }
        return batches;
    }

    private static List<Batch> Compute(Func<string, string> path)
        => Files.OrderBy(f => f, StringComparer.Ordinal).SelectMany(f => BatchesOf(f, File.ReadAllText(path(f)))).ToList();

    /// <summary>
    /// The committed files are what this test produces. Set <c>NID_REGENERATE_VECTORS=1</c> to rewrite them after a
    /// deliberate change to a fixture — never to make a canonicalizer change pass.
    /// </summary>
    [Fact]
    public void The_committed_batch_hashes_are_what_the_canonicalizer_computes()
    {
        if (Environment.GetEnvironmentVariable(RegenerateVariable) == "1")
        {
            File.WriteAllText(Vectors.InRepositoryImport("batching.nid.json"), BatchingFile());
            var regenerated = Compute(f => Vectors.InRepositoryImport(f));
            File.WriteAllText(Vectors.InRepositoryImport("batches.json"), JsonSerializer.Serialize(regenerated, Json) + "\n");
            return;
        }

        Assert.Equal(BatchingFile(), File.ReadAllText(Vectors.CopiedImport("batching.nid.json")));
        var committed = JsonSerializer.Deserialize<List<Batch>>(File.ReadAllText(Vectors.CopiedImport("batches.json")), Json)!;
        Assert.Equal(Compute(f => Vectors.CopiedImport(f)), committed);
    }

    [Fact]
    public void The_batching_file_cuts_into_two_full_batches_and_a_partial_one()
    {
        var batches = BatchesOf("batching.nid.json", BatchingFile());

        Assert.Equal(new[] { 500, 500, 201 }, batches.Select(b => b.Count));
        Assert.Equal(new[] { 0, 1, 2 }, batches.Select(b => b.Index));
    }
}
