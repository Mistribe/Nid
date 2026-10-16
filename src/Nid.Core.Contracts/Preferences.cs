using System.Text.RegularExpressions;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Contracts;

/// <summary>
/// An adult's interface preferences (frontend/design-system.md §7.6). The theme is an opaque identifier: the
/// list of themes belongs to the frontend, which falls back to <c>neutre</c> for one it no longer ships.
/// </summary>
public sealed partial record Preferences(string Theme, string Mode, bool ChartPatterns, IReadOnlyDictionary<string, int> GuidesSeen)
{
    /// <summary>The one empty map, so that a record comparison of two preferences without guides holds.</summary>
    public static IReadOnlyDictionary<string, int> NoGuides { get; } = new Dictionary<string, int>();

    public static Preferences Default { get; } = new("neutre", "system", false, NoGuides);

    public static IReadOnlyList<string> Modes { get; } = ["system", "light", "dark"];

    public Result<Preferences> Apply(PreferencesChange change)
    {
        if (change.Theme is { } theme && !IdPattern().IsMatch(theme))
            return Invalid("A theme is a lowercase identifier of at most 32 characters.");
        if (change.Mode is { } mode && !Modes.Contains(mode))
            return Invalid("A mode is system, light or dark.");
        if (change.GuidesSeen is { } guides && !ValidGuides(guides))
            return Invalid("Guides are at most 32, each a lowercase identifier of at most 32 characters with a version from 1 to 999.");

        return Result.Success(new Preferences(change.Theme ?? Theme, change.Mode ?? Mode, change.ChartPatterns ?? ChartPatterns, change.GuidesSeen ?? GuidesSeen));
    }

    /// <summary>Reads what the database holds; a value that breaks the rules is replaced by its default.</summary>
    public static Preferences FromStored(string? theme, string? mode, bool? chartPatterns, IReadOnlyDictionary<string, int>? guidesSeen)
    {
        var kept = guidesSeen?.Where(g => ValidGuide(g.Key, g.Value)).Take(MaxGuides).ToDictionary(g => g.Key, g => g.Value);
        return new(
            theme is not null && IdPattern().IsMatch(theme) ? theme : Default.Theme,
            mode is not null && Modes.Contains(mode) ? mode : Default.Mode,
            chartPatterns ?? Default.ChartPatterns,
            kept is { Count: > 0 } ? kept : NoGuides);
    }

    private const int MaxGuides = 32;

    private static bool ValidGuide(string id, int version) => IdPattern().IsMatch(id) && version is >= 1 and <= 999;

    private static bool ValidGuides(IReadOnlyDictionary<string, int> guides)
        => guides.Count <= MaxGuides && guides.All(g => ValidGuide(g.Key, g.Value));

    private static Result<Preferences> Invalid(string message)
        => Result.Failure<Preferences>(new Failure("core.preference_invalid", message, null));

    // \z, not $: .NET's $ also matches before a final newline.
    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,31}\z")]
    private static partial Regex IdPattern();
}

/// <summary>A partial update: a null member is left as it is.</summary>
public sealed record PreferencesChange(string? Theme, string? Mode, bool? ChartPatterns, IReadOnlyDictionary<string, int>? GuidesSeen);
