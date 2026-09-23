using System.Text.Json.Serialization;

namespace Nid.Platform.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<IssueSeverity>))]
public enum IssueSeverity
{
    [JsonStringEnumMemberName("error")] Error,
    [JsonStringEnumMemberName("warning")] Warning,
}

/// <summary>
/// One finding of an import. <paramref name="Path"/> names the entity by its global index in its section,
/// as <c>import-format.md</c> §9 shows: <c>purchases[512].grants[0]</c>.
/// </summary>
public sealed record ImportIssue(IssueSeverity Severity, string Code, string Message, string Path)
{
    public static ImportIssue Error(string code, string message, string path) => new(IssueSeverity.Error, code, message, path);

    public static ImportIssue Warning(string code, string message, string path) => new(IssueSeverity.Warning, code, message, path);
}

public sealed record SectionCounts(int Created, int Unchanged, int Conflict)
{
    public static SectionCounts Zero { get; } = new(0, 0, 0);

    public static SectionCounts operator +(SectionCounts a, SectionCounts b)
        => new(a.Created + b.Created, a.Unchanged + b.Unchanged, a.Conflict + b.Conflict);
}

/// <summary>What <c>validate</c> and <c>commit</c> answer, and what the session keeps (§9).</summary>
public sealed record ImportReport(string State, IReadOnlyDictionary<string, SectionCounts> Sections, IReadOnlyList<ImportIssue> Issues);

/// <summary>Stable codes of the import protocol. Module rules keep their own <c>core.</c> and <c>subscriptions.</c> codes.</summary>
public static class ImportCodes
{
    public const string UnsupportedFormat = "import.unsupported_format";
    public const string SessionActive = "import.session_active";
    public const string State = "import.state";
    public const string SlotTaken = "import.slot_taken";
    public const string HashMismatch = "import.hash_mismatch";
    public const string TooManyEntities = "import.too_many_entities";
    public const string StringTooLong = "import.string_too_long";
    public const string UnknownSection = "import.unknown_section";
    public const string SectionNotSupported = "import.section_not_supported";
    public const string MalformedEntity = "import.malformed_entity";
    public const string InvalidValue = "import.invalid_value";
    public const string InvalidId = "import.invalid_id";
    public const string DuplicateId = "import.duplicate_id";
    public const string UnresolvedReference = "import.unresolved_reference";
    public const string BatchLayout = "import.batch_layout";
    public const string Conflict = "import.conflict";
    public const string NotPermitted = "import.not_permitted";
    public const string AdultUnresolved = "import.adult_unresolved";
    public const string AdultMapInvalid = "import.adult_map_invalid";
    public const string AdultDuplicate = "import.adult_duplicate";
    public const string DependentAmbiguous = "import.dependent_ambiguous";
    public const string PhaseNotWholePeriods = "import.phase_not_whole_periods";
    public const string TooLarge = "import.too_large";
    public const string NotCommitted = "import.not_committed";
    public const string ApplyFailed = "import.apply_failed";

    /// <summary>Answered with 409: the request is well formed but the session's state forbids it.</summary>
    public static IReadOnlySet<string> Conflicts { get; } = new HashSet<string> { SessionActive, State, SlotTaken };

    /// <summary>Answered with 400: the request itself is refused before any rule is looked at.</summary>
    public static IReadOnlySet<string> BadRequests { get; } =
        new HashSet<string> { UnsupportedFormat, HashMismatch, TooManyEntities, StringTooLong };
}
