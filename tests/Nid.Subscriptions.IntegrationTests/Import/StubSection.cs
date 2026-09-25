using System.Text.Json;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;

namespace Nid.Subscriptions.IntegrationTests.Import;

/// <summary>A section with one rule — an entity carrying "bad" is refused — to test the protocol alone.</summary>
internal sealed class StubSection : IImportSection
{
    public Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>>(new Dictionary<string, IReadOnlyList<JsonElement>>());

    public const string Name = "stub_things";

    public string Module => "stub";
    public IReadOnlyList<string> Sections => [Name];
    public int Order => 50;

    public IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex)
        => entities.EnumerateArray()
            .Select((entity, position) => (entity, position))
            .Where(x => x.entity.TryGetProperty("bad", out _))
            .Select(x => ImportIssue.Error("stub.bad", "Refused on purpose.", NidDataJson.Path(Name, firstIndex + x.position, "bad")))
            .ToList();

    public Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
        => Task.FromResult(new SectionOutcome([], Counts(staged)));

    public Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
        => Task.FromResult(Result.Success(Counts(staged)));

    private static IReadOnlyDictionary<string, SectionCounts> Counts(IStagedBatches staged)
        => new Dictionary<string, SectionCounts> { [Name] = new(staged.Section(Name).Count, 0, 0) };
}
