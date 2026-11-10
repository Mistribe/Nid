using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Core.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Infrastructure.Persistence;

/// <summary>tokens-and-credentials.md §1 on <c>core.personal_access_token</c> (spec 4A §2).</summary>
public sealed class PersonalAccessTokenStore(CoreDbContext db, IClock clock) : IPersonalAccessTokens
{
    public async Task<Result<NewToken>> CreateAsync(PersonId person, string name, string preset, int? expiresInDays, CancellationToken ct)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > TokenLimits.NameMax)
            return Refuse(TokenCodes.NameInvalid, "A token's name is 1 to 80 characters.", "name");
        if (!TokenPresets.IsBearer(preset))
            return Refuse(TokenCodes.PresetUnknown, "The preset is read, propose or direct.", "preset");
        var days = expiresInDays ?? TokenLimits.DefaultDays;
        if (days is < 1 or > TokenLimits.MaxDays)
            return Refuse(TokenCodes.ExpiryInvalid, "A token expires in 1 to 365 days.", "expiresInDays");
        return await AddAsync(person, trimmed, preset, days, null, ct);
    }

    public Task<Result<NewToken>> CreateCalendarAsync(PersonId person, CalendarFeedOptions options, CancellationToken ct)
        => AddAsync(person, CalendarFeedLimits.Name, TokenPresets.Calendar, CalendarFeedLimits.Days, options, ct);

    private async Task<Result<NewToken>> AddAsync(PersonId person, string name, string preset, int days, CalendarFeedOptions? calendar, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Two creations for the same person must not both see nine active tokens (Review Focus 3), nor both keep a calendar feed.
        await db.Database.ExecuteSqlAsync($"select 1 from core.person where id = {person.Value} for update", ct);
        var now = clock.GetCurrentInstant();
        // Issue #80: one feed per person, so a new one is a regeneration: the old URL stops at once.
        if (calendar is not null)
            await Active(person, now).Where(t => t.CalendarScope != null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        if (await Active(person, now).CountAsync(ct) >= TokenLimits.MaxActive)
            return Result.Failure<NewToken>(new Failure(TokenCodes.LimitReached, "A person holds at most 10 active tokens.", null));

        var (token, prefix, hash) = calendar is null ? TokenFormat.Generate() : TokenFormat.GenerateCalendar();
        var row = new PersonalAccessTokenRow
        {
            Id = Guid.CreateVersion7(), PersonId = person.Value, Name = name, Prefix = prefix, TokenHash = hash,
            Scopes = TokenPresets.Stored(preset), CreatedAt = now, ExpiresAt = now.Plus(NodaTime.Duration.FromDays(days)),
            CalendarScope = calendar is null ? null : ScopeText(calendar.Scope), CalendarAmounts = calendar?.Amounts,
        };
        db.PersonalAccessTokens.Add(row);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Success(new NewToken(ToToken(row), token));
    }

    public async Task<Result<PersonalAccessToken>> SetCalendarOptionsAsync(PersonId person, CalendarFeedOptions options, CancellationToken ct)
    {
        var row = await Active(person, clock.GetCurrentInstant()).Where(t => t.CalendarScope != null).SingleOrDefaultAsync(ct);
        if (row is null)
            return Result.Failure<PersonalAccessToken>(new NotFoundFailure("calendar", person.Value.ToString()));
        row.CalendarScope = ScopeText(options.Scope);
        row.CalendarAmounts = options.Amounts;
        await db.SaveChangesAsync(ct);
        return Result.Success(ToToken(row));
    }

    public async Task<CalendarCredential?> FindCalendarByPrefixAsync(string prefix, CancellationToken ct)
    {
        var row = await db.PersonalAccessTokens.AsNoTracking().Where(t => t.Prefix == prefix && t.CalendarScope != null).SingleOrDefaultAsync(ct);
        return row is null ? null : new CalendarCredential(row.Id, new PersonId(row.PersonId), row.TokenHash, OptionsOf(row)!, row.ExpiresAt, row.RevokedAt);
    }

    private IQueryable<PersonalAccessTokenRow> Active(PersonId person, Instant now)
        => db.PersonalAccessTokens.Where(t => t.PersonId == person.Value && t.RevokedAt == null && t.ExpiresAt > now);

    private static string ScopeText(VisibilityScope scope) => scope == VisibilityScope.Me ? "me" : "household";

    private static CalendarFeedOptions? OptionsOf(PersonalAccessTokenRow r)
        => r.CalendarScope is { } scope ? new CalendarFeedOptions(scope == "me" ? VisibilityScope.Me : VisibilityScope.Household, r.CalendarAmounts == true) : null;

    public async Task<IReadOnlyList<PersonalAccessToken>> ListAsync(PersonId person, CancellationToken ct)
        => (await db.PersonalAccessTokens.AsNoTracking().Where(t => t.PersonId == person.Value)
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).ToListAsync(ct)).Select(ToToken).ToList();

    public async Task<Result<bool>> RevokeAsync(PersonId person, Guid token, CancellationToken ct)
    {
        var row = await db.PersonalAccessTokens.SingleOrDefaultAsync(t => t.Id == token && t.PersonId == person.Value, ct);
        if (row is null)
            return Result.Failure<bool>(new NotFoundFailure("token", token.ToString()));
        if (row.RevokedAt is not null || row.ExpiresAt <= clock.GetCurrentInstant())
            return Result.Success(false);
        row.RevokedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
        return Result.Success(true);
    }

    public Task<int> RevokeAllAsync(PersonId person, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        return db.PersonalAccessTokens.Where(t => t.PersonId == person.Value && t.RevokedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    public async Task<TokenCredential?> FindByPrefixAsync(string prefix, CancellationToken ct)
        => await db.PersonalAccessTokens.AsNoTracking().Where(t => t.Prefix == prefix && t.CalendarScope == null)
            .Select(t => new TokenCredential(t.Id, new PersonId(t.PersonId), t.TokenHash, t.Scopes.ToHashSet(), t.ExpiresAt, t.RevokedAt))
            .SingleOrDefaultAsync(ct);

    public Task TouchAsync(Guid token, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var minuteAgo = now.Minus(NodaTime.Duration.FromMinutes(1));
        return db.PersonalAccessTokens.Where(t => t.Id == token && (t.LastUsedAt == null || t.LastUsedAt <= minuteAgo))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), ct);
    }

    private static Result<NewToken> Refuse(string code, string message, string path)
        => Result.Failure<NewToken>(FieldErrors.At(new Failure(code, message, null), path));

    private static PersonalAccessToken ToToken(PersonalAccessTokenRow r) => new(
        r.Id, new PersonId(r.PersonId), r.Name, r.Prefix, r.Scopes.ToHashSet(StringComparer.Ordinal),
        r.CreatedAt, r.ExpiresAt, r.LastUsedAt, r.RevokedAt, OptionsOf(r));
}
