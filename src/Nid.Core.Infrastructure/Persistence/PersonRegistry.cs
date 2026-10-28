using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Infrastructure.Persistence;

public sealed class PersonRegistry(CoreDbContext db, IClock clock) : IPersonRegistry
{
    /// <summary>Serialises everything that counts adults or admins: first logins, pre-registration, the cap, demotions.</summary>
    internal const long ProvisioningLockKey = 0x4E49_4401; // "NID" + 1

    public async Task<Result<PersonInfo>> ProvisionAdultAsync(
        string oidcSubject, string displayName, string? email, bool isAdmin, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct);

        var address = string.IsNullOrWhiteSpace(email) ? null : email.Trim();

        var existing = await db.Persons.SingleOrDefaultAsync(p => p.OidcSubject == oidcSubject, ct);
        if (existing is not null)
        {
            existing.DisplayName = displayName;
            existing.IsAdmin = isAdmin;
            // A claim that now carries another adult's email must not break the unique index: keep the old one.
            if (address is null || !await EmailTakenAsync(address, existing.Id, ct))
                existing.Email = address;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Result.Success(ToInfo(existing));
        }

        var normalized = address?.ToLowerInvariant();
        var pending = normalized is null
            ? null
            : await db.Persons.SingleOrDefaultAsync(
                p => p.Kind == "adult" && p.OidcSubject == null && p.Email != null && p.Email.ToLower() == normalized, ct);
        if (pending is not null)
        {
            pending.OidcSubject = oidcSubject;
            pending.DisplayName = displayName;
            pending.Email = address;
            pending.IsAdmin = isAdmin;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Result.Success(ToInfo(pending));
        }

        // Another adult already has this email under another subject: the same person whose subject changed, or someone
        // else. Not for a sign-in to decide: an admin rebinds the subject (1F4).
        if (address is not null && await EmailTakenAsync(address, null, ct))
            return Fail(PersonCodes.EmailTaken, "Another adult already has this email address.");

        var cap = await CapAsync(ct);
        if (await db.Persons.CountAsync(p => p.Kind == "adult", ct) >= cap)
            return Fail(PersonCodes.AdultCap, $"This household is limited to {cap} adults.");

        var created = NewAdult(oidcSubject, displayName, address, isAdmin);
        db.Persons.Add(created);
        if (!await db.HouseholdSettings.AnyAsync(ct))
            db.HouseholdSettings.Add(new HouseholdSettingsRow());
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Success(ToInfo(created));
    }

    public async Task<PersonInfo?> FindBySubjectAsync(string oidcSubject, CancellationToken ct)
    {
        var row = await db.Persons.AsNoTracking().SingleOrDefaultAsync(p => p.OidcSubject == oidcSubject, ct);
        return row is null ? null : ToInfo(row);
    }

    private async Task<int> CapAsync(CancellationToken ct)
        => await db.HouseholdSettings.Select(s => (int?)s.MaxAdults).FirstOrDefaultAsync(ct) ?? new HouseholdSettingsRow().MaxAdults;

    public async Task<Result<PersonInfo>> PreRegisterAdultAsync(string displayName, string email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return Fail(PersonCodes.NameRequired, "A name is required.");
        var address = email?.Trim() ?? "";
        if (!LooksLikeEmail(address))
            return Fail(PersonCodes.EmailRequired, "An email address is required: it is how the person is recognised at their first sign-in.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct);

        if (await EmailTakenAsync(address, null, ct))
            return Fail(PersonCodes.EmailTaken, "Another adult already has this email address.");
        var cap = await CapAsync(ct);
        if (await db.Persons.CountAsync(p => p.Kind == "adult", ct) >= cap)
            return Fail(PersonCodes.AdultCap, $"This household is limited to {cap} adults.");

        var row = NewAdult(null, displayName.Trim(), address, isAdmin: false);
        db.Persons.Add(row);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Success(ToInfo(row));
    }

    private Task LockAsync(CancellationToken ct)
        => db.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({ProvisioningLockKey})", ct);

    private Task<bool> EmailTakenAsync(string email, Guid? except, CancellationToken ct)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return db.Persons.AnyAsync(
            p => p.Kind == "adult" && p.Email != null && p.Email.ToLower() == normalized && (except == null || p.Id != except.Value), ct);
    }

    /// <summary>Only what the binding needs: one '@' with something on each side, no space. The identity provider owns the real address.</summary>
    private static bool LooksLikeEmail(string value)
    {
        var at = value.IndexOf('@');
        return value.Length <= 254 && at > 0 && at == value.LastIndexOf('@') && at < value.Length - 1 && !value.Any(char.IsWhiteSpace);
    }

    private PersonRow NewAdult(string? subject, string displayName, string? email, bool isAdmin) => new()
    {
        Id = PersonId.New().Value,
        Kind = "adult",
        OidcSubject = subject,
        DisplayName = displayName,
        Email = email,
        IsAdmin = isAdmin,
        CreatedAt = clock.GetCurrentInstant(),
    };

    private static Result<PersonInfo> Fail(string code, string message)
        => Result.Failure<PersonInfo>(new Failure(code, message, null));

    public async Task<Result<PersonInfo>> CreateDependentAsync(string displayName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return Result.Failure<PersonInfo>(new Failure(PersonCodes.NameRequired, "A name is required.", null));

        var row = new PersonRow
        {
            Id = PersonId.New().Value,
            Kind = "dependent",
            DisplayName = displayName.Trim(),
            CreatedAt = clock.GetCurrentInstant(),
        };
        db.Persons.Add(row);
        await db.SaveChangesAsync(ct);
        return Result.Success(ToInfo(row));
    }

    public async Task<PersonInfo?> FindAsync(PersonId id, CancellationToken ct)
    {
        var row = await db.Persons.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id.Value, ct);
        return row is null ? null : ToInfo(row);
    }

    public async Task<IReadOnlyList<PersonId>> AdultIdsAsync(CancellationToken ct)
        => await db.Persons.AsNoTracking().Where(p => p.Kind == "adult").Select(p => new PersonId(p.Id)).ToListAsync(ct);

    public async Task<IReadOnlyList<PersonInfo>> ListAsync(CancellationToken ct)
        => (await db.Persons.AsNoTracking().OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).ToListAsync(ct)).Select(ToInfo).ToList();

    public async Task<Preferences?> PreferencesAsync(PersonId id, CancellationToken ct)
    {
        var stored = await db.Persons.AsNoTracking().Where(p => p.Id == id.Value).Select(p => p.Preferences).SingleOrDefaultAsync(ct);
        return stored is null ? null : ReadPreferences(JsonNode.Parse(stored) as JsonObject ?? []);
    }

    public async Task<Result<Preferences>> UpdatePreferencesAsync(PersonId id, PreferencesChange change, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Two tabs, or two sign-ins, changing preferences at once: each applies its change to what the other saved.
        await db.Database.ExecuteSqlAsync($"select 1 from core.person where id = {id.Value} for update", ct);

        var row = await db.Persons.SingleOrDefaultAsync(p => p.Id == id.Value, ct);
        if (row is null)
            return Result.Failure<Preferences>(new NotFoundFailure("person", id.ToString()));

        var node = JsonNode.Parse(row.Preferences) as JsonObject ?? [];
        var applied = ReadPreferences(node).Apply(change);
        if (!applied.TryGetValue(out var preferences))
            return applied;

        node["theme"] = preferences.Theme;
        node["mode"] = preferences.Mode;
        node["chart_patterns"] = preferences.ChartPatterns;
        node["guides_seen"] = new JsonObject(preferences.GuidesSeen.Select(g => KeyValuePair.Create(g.Key, (JsonNode?)JsonValue.Create(g.Value))));
        row.Preferences = node.ToJsonString();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Success(preferences);
    }

    private static Preferences ReadPreferences(JsonObject node) => Preferences.FromStored(
        node["theme"]?.GetValueKind() == JsonValueKind.String ? node["theme"]!.GetValue<string>() : null,
        node["mode"]?.GetValueKind() == JsonValueKind.String ? node["mode"]!.GetValue<string>() : null,
        node["chart_patterns"]?.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? node["chart_patterns"]!.GetValue<bool>() : null,
        node["guides_seen"] is JsonObject guides
            ? guides.Where(g => g.Value?.GetValueKind() == JsonValueKind.Number && g.Value.AsValue().TryGetValue<int>(out _))
                .ToDictionary(g => g.Key, g => g.Value!.GetValue<int>())
            : null);

    public async Task<Result> DeletePendingAdultAsync(PersonId id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialised against a first login or an import binding this same pending adult (both take the same
        // lock): neither can turn it active out from under this delete, and this delete cannot remove it out
        // from under either.
        await LockAsync(ct);

        var row = await db.Persons.SingleOrDefaultAsync(p => p.Id == id.Value && p.Kind == "adult", ct);
        if (row is null)
            return Result.Failure(new NotFoundFailure("person", id.ToString()));
        if (row.OidcSubject is not null)
            return Result.Failure(new Failure(PersonCodes.PersonActive, "Only an adult who has not signed in yet can be removed.", null));
        // The import resolves adults by email, pending ones included (import-format.md §5.1).
        if (await db.Ownerships.AnyAsync(o => o.PersonId == row.Id, ct))
            return Result.Failure(new Failure(PersonCodes.PersonActive, "This adult already owns items: they cannot be removed.", null));

        if (await db.TaxAssessmentDeclarants.AnyAsync(d => d.PersonId == row.Id, ct))
            return Result.Failure(new Failure(PersonCodes.PersonActive, "This adult declares a tax assessment: they cannot be removed.", null));

        db.Persons.Remove(row);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    public async Task<Result<PersonInfo>> RevokeSessionsAsync(PersonId id, CancellationToken ct)
    {
        var changed = await db.Persons.Where(p => p.Id == id.Value && p.Kind == "adult")
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.SessionEpoch, p => p.SessionEpoch + 1), ct);
        if (changed == 0)
            return Result.Failure<PersonInfo>(new NotFoundFailure("person", id.ToString()));

        var row = await db.Persons.AsNoTracking().SingleAsync(p => p.Id == id.Value, ct);
        return Result.Success(ToInfo(row));
    }

    public async Task<Result<RebindOutcome>> RebindSubjectAsync(PersonId id, string subject, CancellationToken ct)
    {
        // A pasted subject keeps no surrounding space: it would never match a sign-in (final review).
        subject = subject.Trim();
        if (subject.Length == 0 || subject.Length > 255)
            return Result.Failure<RebindOutcome>(new Failure(PersonCodes.SubjectInvalid, "A subject is 1 to 255 characters."));

        var row = await db.Persons.SingleOrDefaultAsync(p => p.Id == id.Value, ct);
        if (row is null)
            return Result.Failure<RebindOutcome>(new NotFoundFailure("person", id.ToString()));
        if (row.Kind != "adult")
            return Result.Failure<RebindOutcome>(new Failure(PersonCodes.NotAnAdult, "Only an adult signs in."));
        if (row.OidcSubject == subject)
            return Result.Success(new RebindOutcome(ToInfo(row), row.OidcSubject, false));

        var holder = await db.Persons.AsNoTracking().Where(p => p.OidcSubject == subject && p.Id != id.Value).Select(p => p.Id).FirstOrDefaultAsync(ct);
        if (holder != Guid.Empty)
            return Result.Failure<RebindOutcome>(Taken(holder));

        var previous = row.OidcSubject;
        row.OidcSubject = subject;
        row.SessionEpoch += 1;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Review Focus 2: a sign-in took the subject between the check and the write; the unique index decides.
            db.ChangeTracker.Clear();
            var winner = await db.Persons.AsNoTracking().Where(p => p.OidcSubject == subject).Select(p => p.Id).FirstAsync(ct);
            return Result.Failure<RebindOutcome>(Taken(winner));
        }
        return Result.Success(new RebindOutcome(ToInfo(row), previous, true));
    }

    private static Failure Taken(Guid holder) => new(PersonCodes.SubjectTaken, "Another person holds this subject.",
        new Dictionary<string, object?> { ["person"] = holder.ToString() });

    private static PersonInfo ToInfo(PersonRow row) => new(
        new PersonId(row.Id),
        row.Kind == "adult" ? PersonKind.Adult : PersonKind.Dependent,
        row.DisplayName,
        row.Email,
        row.SessionEpoch,
        row.IsAdmin,
        row.Kind == "adult" && row.OidcSubject is null);
}
