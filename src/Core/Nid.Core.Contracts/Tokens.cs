using NodaTime;
using UnambitiousFx.Functional;

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
}

/// <summary>Issue #80: what a calendar feed lists: the events of the items its person reads in <c>Scope</c>, amounts only when <c>Amounts</c>.</summary>
public sealed record CalendarFeedOptions(VisibilityScope Scope, bool Amounts);

public static class CalendarFeedLimits
{
    /// <summary>A feed lives as long as the longest token, from its creation or its last regeneration.</summary>
    public const int Days = TokenLimits.MaxDays;

    /// <summary>The feed's window, from today.</summary>
    public const int Months = 12;

    /// <summary>The feed's row in « Mes jetons ».</summary>
    public const string Name = "Calendrier";
}

/// <summary>What the feed route needs of a calendar row: never sent anywhere.</summary>
public sealed record CalendarCredential(Guid Id, PersonId Person, string Hash, CalendarFeedOptions Options, Instant ExpiresAt, Instant? RevokedAt);

public sealed record PersonalAccessToken(
    Guid Id, PersonId Person, string Name, string Prefix, IReadOnlySet<string> Scopes,
    Instant CreatedAt, Instant ExpiresAt, Instant? LastUsedAt, Instant? RevokedAt, CalendarFeedOptions? Calendar = null);

/// <summary>What authentication needs of a row: never the name, never sent anywhere.</summary>
public sealed record TokenCredential(Guid Id, PersonId Person, string Hash, IReadOnlySet<string> Scopes, Instant ExpiresAt, Instant? RevokedAt);

/// <summary>A token just created: <see cref="Secret"/> exists here and nowhere else.</summary>
public sealed record NewToken(PersonalAccessToken Token, string Secret);

public interface IPersonalAccessTokens
{
    /// <summary>Validates name, preset and expiry (field failures), then creates under a lock on the person (10 active at most).</summary>
    Task<Result<NewToken>> CreateAsync(PersonId person, string name, string preset, int? expiresInDays, CancellationToken ct);

    /// <summary>Every token of the person, newest first, revoked and expired ones included.</summary>
    Task<IReadOnlyList<PersonalAccessToken>> ListAsync(PersonId person, CancellationToken ct);

    /// <summary><c>true</c> when an active token was revoked, <c>false</c> when it already was not active; not found for another's.</summary>
    Task<Result<bool>> RevokeAsync(PersonId person, Guid token, CancellationToken ct);

    /// <summary>Revokes every active token of the person; the count revoked.</summary>
    Task<int> RevokeAllAsync(PersonId person, CancellationToken ct);

    /// <summary>A bearer token's row; never a calendar feed's.</summary>
    Task<TokenCredential?> FindByPrefixAsync(string prefix, CancellationToken ct);

    /// <summary>
    /// Issue #80: creates the person's calendar feed under the same lock and limit, revoking the active one first (a regeneration).
    /// Its secret is a <c>nid_cal_…</c>, which bearer authentication never reads.
    /// </summary>
    Task<Result<NewToken>> CreateCalendarAsync(PersonId person, CalendarFeedOptions options, CancellationToken ct);

    /// <summary>Changes the active feed's options, its URL unchanged; not found when the person has none.</summary>
    Task<Result<PersonalAccessToken>> SetCalendarOptionsAsync(PersonId person, CalendarFeedOptions options, CancellationToken ct);

    Task<CalendarCredential?> FindCalendarByPrefixAsync(string prefix, CancellationToken ct);

    /// <summary>Sets <c>last_used_at</c> to now when it is unset or older than a minute (spec D3).</summary>
    Task TouchAsync(Guid token, CancellationToken ct);
}
