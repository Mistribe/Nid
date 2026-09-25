using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Platform.Contracts;

/// <summary>One entity of a section, as staged. <paramref name="Index"/> is its global index in the section.</summary>
public sealed record StagedEntity(int Index, JsonElement Value, string Hash);

public interface IStagedBatches
{
    /// <summary>Every staged entity of the section, in index order; empty when none was sent.</summary>
    IReadOnlyList<StagedEntity> Section(string section);
}

public sealed record ResolvedPerson(PersonId Id, PersonKind Kind);

/// <summary>
/// What sections share during one validation or commit. Sections fill it in <see cref="IImportSection.Order"/>:
/// <c>persons</c> resolves file refs to persons, item-creating sections map their file ids to items, and later
/// sections read both. During <c>validate</c> the ids of what does not exist yet are planned, never written;
/// <c>ApplyAsync</c> replaces them with the real ones.
/// </summary>
public sealed class ImportContext(PersonId actor, IReadOnlyDictionary<string, PersonId> adultMap, LocalDate today)
{
    public PersonId Actor { get; } = actor;

    /// <summary>File ref of an adult → the account it is, as given when the session was created.</summary>
    public IReadOnlyDictionary<string, PersonId> AdultMap { get; } = adultMap;

    public LocalDate Today { get; } = today;

    public Dictionary<string, ResolvedPerson> Persons { get; } = new(StringComparer.Ordinal);

    public Dictionary<(string Section, string Id), ItemId> Items { get; } = [];
}

public sealed record SectionOutcome(IReadOnlyList<ImportIssue> Issues, IReadOnlyDictionary<string, SectionCounts> Counts);

/// <summary>
/// A module's share of <c>nid.data</c> (<c>import-format.md</c> §11.7). Platform calls, in <see cref="Order"/>,
/// <see cref="ValidateAllAsync"/> on every section, then — only at commit, only if no section reported an
/// error, inside the one transaction — <see cref="ApplyAsync"/> on the same instances. A section may keep
/// what it planned between the two calls. The export (1D2) calls <see cref="ExportAsync"/>, outside any import.
/// </summary>
public interface IImportSection
{
    string Module { get; }

    IReadOnlyList<string> Sections { get; }

    int Order { get; }

    /// <summary>Rules local to each entity, on receipt. <paramref name="firstIndex"/> is the global index of the first.</summary>
    IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex);

    Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct);

    Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct);

    /// <summary>
    /// Every record of each of <see cref="Sections"/> that <see cref="ExportContext.Reader"/> may export, in a stable
    /// order, as file JSON (1D2 design §2). Platform pages them; a section returns its whole sections every time.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct);
}

/// <summary>How every section reads its entities: <c>snake_case</c>, unknown members refused (§2 "strict").</summary>
public static partial class NidDataJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex FileIdPattern();

    /// <summary><c>import-format.md</c> §4: a stable file key.</summary>
    public static bool IsFileId([NotNullWhen(true)] string? id) => id is not null && FileIdPattern().IsMatch(id);

    public static string Path(string section, int index, string? member = null)
        => member is null ? $"{section}[{index}]" : $"{section}[{index}].{member}";

    /// <summary>Reads one entity; on failure adds an issue at <paramref name="path"/> plus the member that failed.</summary>
    public static bool TryRead<T>(JsonElement element, string path, ICollection<ImportIssue> issues, [NotNullWhen(true)] out T? value)
        where T : class
    {
        try
        {
            value = element.Deserialize<T>(Options);
        }
        catch (JsonException e)
        {
            issues.Add(ImportIssue.Error(ImportCodes.MalformedEntity, e.Message, path + Member(e.Path)));
            value = null;
            return false;
        }

        if (value is null)
            issues.Add(ImportIssue.Error(ImportCodes.MalformedEntity, "An entity is an object, not null.", path));
        return value is not null;
    }

    private static string Member(string? jsonPath)
        => jsonPath is null or "$" ? "" : jsonPath.StartsWith("$.", StringComparison.Ordinal) ? "." + jsonPath[2..] : jsonPath[1..];
}
