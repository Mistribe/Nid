namespace Nid.Core.Contracts;

/// <summary>tokens-and-credentials.md §1.3: the only scope sets a token may hold.</summary>
public static class TokenPresets
{
    public const string Read = "read";
    public const string Propose = "propose";
    public const string Direct = "direct";

    /// <summary>Issue #80: a calendar feed's secret (<c>nid_cal_…</c>): it reads that feed and nothing else, and is never a bearer token.</summary>
    public const string Calendar = "calendar";

    private static readonly Dictionary<string, string[]> Scopes = new(StringComparer.Ordinal)
    {
        [Read] = ["read"],
        [Propose] = ["read", "write"],
        [Direct] = ["read", "write", "write:direct"],
        [Calendar] = ["calendar"],
    };

    /// <summary>The presets « Mes jetons » creates; a calendar feed is made from Réglages › Calendrier.</summary>
    public static bool IsBearer(string? preset) => preset is Read or Propose or Direct;

    public static IReadOnlySet<string>? ScopesOf(string preset)
        => Scopes.TryGetValue(preset, out var scopes) ? scopes.ToHashSet(StringComparer.Ordinal) : null;

    /// <summary>The scope array as stored (sorted, ordinal), for the check constraint and the row.</summary>
    public static string[] Stored(string preset) => [.. Scopes[preset].Order(StringComparer.Ordinal)];

    public static string Of(IEnumerable<string> scopes)
    {
        var set = scopes.ToHashSet(StringComparer.Ordinal);
        return Scopes.First(p => set.SetEquals(p.Value)).Key;
    }
}

public static class TokenLimits
{
    public const int MaxActive = 10;
    public const int DefaultDays = 90;
    public const int MaxDays = 365;
    public const int NameMax = 80;
}

public static class TokenCodes
{
    public const string LimitReached = "core.token_limit_reached";
    public const string NameInvalid = "core.token_name_invalid";
    public const string PresetUnknown = "core.token_preset_unknown";
    public const string ExpiryInvalid = "core.token_expiry_invalid";
    public const string CalendarScopeInvalid = "core.calendar_scope_invalid";
    public const string CalendarModulesInvalid = "core.calendar_modules_invalid";

    /// <summary>Issue #239: the calendar page's dates (before the current month, reversed or too far apart).</summary>
    public const string CalendarRangeInvalid = "core.calendar_range_invalid";
}

/// <summary>
/// Issue #80: what a calendar feed lists: the events of the items its person reads in <c>Scope</c>, amounts only when <c>Amounts</c>.
/// Issue #243: of the <see cref="CalendarModules"/> in <c>Modules</c>; null is every module, one added later included.
/// </summary>
public sealed record CalendarFeedOptions(VisibilityScope Scope, bool Amounts, IReadOnlyList<string>? Modules = null);

public static class CalendarFeedLimits
{
    /// <summary>A feed lives as long as the longest token, from its creation or its last regeneration.</summary>
    public const int Days = TokenLimits.MaxDays;

    /// <summary>The feed's window, from today.</summary>
    public const int Months = 12;

    /// <summary>The feed's row in « Mes jetons ».</summary>
    public const string Name = "Calendrier";
}
