using System.Text.RegularExpressions;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Contracts;

/// <summary>
/// An adult's interface preferences (frontend/design-system.md §7.6). The theme is an opaque identifier: the
/// list of themes belongs to the frontend, which falls back to <c>neutre</c> for one it no longer ships.
/// </summary>
public sealed partial record Preferences(string Theme, string Mode, bool ChartPatterns)
{
    public static Preferences Default { get; } = new("neutre", "system", false);

    public static IReadOnlyList<string> Modes { get; } = ["system", "light", "dark"];

    public Result<Preferences> Apply(PreferencesChange change)
    {
        if (change.Theme is { } theme && !ThemePattern().IsMatch(theme))
            return Invalid("A theme is a lowercase identifier of at most 32 characters.");
        if (change.Mode is { } mode && !Modes.Contains(mode))
            return Invalid("A mode is system, light or dark.");

        return Result.Success(new Preferences(change.Theme ?? Theme, change.Mode ?? Mode, change.ChartPatterns ?? ChartPatterns));
    }

    /// <summary>Reads what the database holds; a value that breaks the rules is replaced by its default.</summary>
    public static Preferences FromStored(string? theme, string? mode, bool? chartPatterns) => new(
        theme is not null && ThemePattern().IsMatch(theme) ? theme : Default.Theme,
        mode is not null && Modes.Contains(mode) ? mode : Default.Mode,
        chartPatterns ?? Default.ChartPatterns);

    private static Result<Preferences> Invalid(string message)
        => Result.Failure<Preferences>(new Failure("core.preference_invalid", message, null));

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex ThemePattern();
}

/// <summary>A partial update: a null member is left as it is.</summary>
public sealed record PreferencesChange(string? Theme, string? Mode, bool? ChartPatterns);
