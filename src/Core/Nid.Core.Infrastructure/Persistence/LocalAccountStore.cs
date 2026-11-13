using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Infrastructure.Persistence;

/// <summary>Issue 215, identity-and-access/local-accounts.md §13: local accounts and their one-time links.</summary>
public sealed class LocalAccountStore(CoreDbContext db, IClock clock) : ILocalAccountStore
{
    public const string LinkPrefix = "nid_link_";

    public async Task<LocalCredential?> FindByLoginAsync(string login, CancellationToken ct)
    {
        var row = await db.LocalCredentials.AsNoTracking().SingleOrDefaultAsync(c => c.Login == login, ct);
        return row is null ? null : ToCredential(row);
    }

    public async Task<LocalCredential?> FindAsync(PersonId person, CancellationToken ct)
    {
        var row = await db.LocalCredentials.AsNoTracking().SingleOrDefaultAsync(c => c.PersonId == person.Value, ct);
        return row is null ? null : ToCredential(row);
    }

    public async Task<FailureOutcome> RecordFailureAsync(
        PersonId person, Instant now, int threshold, NodaTime.Duration lockBase, NodaTime.Duration max, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Two wrong passwords at once must both count: the row is locked while it is read and written.
        var row = await db.LocalCredentials.FromSql($"select * from core.local_credential where person_id = {person.Value} for update")
            .SingleOrDefaultAsync(ct);
        if (row is null)
            return new FailureOutcome(false, 0, null);
        row.LastFailedAt = now;
        row.FailedCount++;
        var locked = false;
        if (row.FailedCount >= threshold)
        {
            row.LockLevel++;
            var minutes = Math.Min(lockBase.TotalMinutes * Math.Pow(2, row.LockLevel - 1), max.TotalMinutes);
            row.LockedUntil = now.Plus(NodaTime.Duration.FromMinutes(minutes));
            row.FailedCount = 0;
            locked = true;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new FailureOutcome(locked, row.LockLevel, row.LockedUntil);
    }

    public async Task RecordSuccessAsync(PersonId person, Instant now, string? rehash, CancellationToken ct)
    {
        var hash = rehash;
        await db.LocalCredentials.Where(c => c.PersonId == person.Value).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.FailedCount, 0)
            .SetProperty(c => c.LockLevel, 0)
            .SetProperty(c => c.LockedUntil, (Instant?)null)
            .SetProperty(c => c.LastLoginAt, now)
            .SetProperty(c => c.PasswordHash, c => hash ?? c.PasswordHash), ct);
    }

    public async Task<bool> UnlockAsync(PersonId person, CancellationToken ct)
        => await db.LocalCredentials.Where(c => c.PersonId == person.Value).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.FailedCount, 0)
            .SetProperty(c => c.LockLevel, 0)
            .SetProperty(c => c.LockedUntil, (Instant?)null), ct) > 0;

    public async Task<Result<NewCredentialLink>> CreateLinkAsync(PersonId person, PersonId? createdBy, string via, NodaTime.Duration lifetime, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Two links created at once for the same adult: the person's row lock makes the second revoke the first.
        await db.Database.ExecuteSqlAsync($"select 1 from core.person where id = {person.Value} for update", ct);
        var kind = await db.Persons.AsNoTracking().Where(p => p.Id == person.Value).Select(p => p.Kind).SingleOrDefaultAsync(ct);
        if (kind != "adult")
            return Result.Failure<NewCredentialLink>(new NotFoundFailure("person", person.ToString()));

        var now = clock.GetCurrentInstant();
        await Live(person).ExecuteUpdateAsync(s => s.SetProperty(l => l.RevokedAt, now), ct);
        var purpose = await db.LocalCredentials.AnyAsync(c => c.PersonId == person.Value, ct)
            ? CredentialLinkPurposes.Reset
            : CredentialLinkPurposes.Enroll;
        var token = LinkPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var row = new CredentialLinkRow
        {
            Id = Guid.CreateVersion7(), PersonId = person.Value, Purpose = purpose, TokenHash = Hash(token),
            CreatedBy = createdBy?.Value, Via = via, CreatedAt = now, ExpiresAt = now.Plus(lifetime),
        };
        db.CredentialLinks.Add(row);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Success(new NewCredentialLink(row.Id, token, purpose, row.ExpiresAt));
    }

    public async Task<bool> RevokeLinkAsync(PersonId person, CancellationToken ct)
        => await Live(person).ExecuteUpdateAsync(s => s.SetProperty(l => l.RevokedAt, clock.GetCurrentInstant()), ct) > 0;

    public async Task<CredentialLinkInfo?> FindLinkAsync(string token, Instant now, CancellationToken ct)
    {
        if (!LooksLikeLink(token))
            return null;
        var hash = Hash(token);
        var found = await (from link in db.CredentialLinks.AsNoTracking()
                           join person in db.Persons.AsNoTracking() on link.PersonId equals person.Id
                           where link.TokenHash == hash && link.UsedAt == null && link.RevokedAt == null && link.ExpiresAt > now
                           select new { link.Id, link.PersonId, link.Purpose, person.DisplayName, person.Email })
            .SingleOrDefaultAsync(ct);
        return found is null ? null : new CredentialLinkInfo(found.Id, new PersonId(found.PersonId), found.Purpose, found.DisplayName, found.Email);
    }

    public async Task<Result<CompletedLink>> CompleteLinkAsync(string token, string? login, string passwordHash, Instant now, CancellationToken ct)
    {
        if (!LooksLikeLink(token))
            return NotFound();
        var hash = Hash(token);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // The same lock as a first sign-in: an enrolment turns a pending adult active, which an OIDC binding or a delete
        // of the pending adult must not race.
        await db.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({PersonRegistry.ProvisioningLockKey})", ct);
        var link = await db.CredentialLinks.FromSql($"select * from core.credential_link where token_hash = {hash} for update").SingleOrDefaultAsync(ct);
        if (link is null || link.UsedAt is not null || link.RevokedAt is not null || link.ExpiresAt <= now)
            return NotFound();
        var person = await db.Persons.SingleAsync(p => p.Id == link.PersonId, ct);
        var credential = await db.LocalCredentials.SingleOrDefaultAsync(c => c.PersonId == link.PersonId, ct);

        if (credential is null)
        {
            if (string.IsNullOrEmpty(login))
                return Result.Failure<CompletedLink>(new Failure(LocalAccountCodes.LoginInvalid, "An identifier is required.", null));
            if (await db.LocalCredentials.AnyAsync(c => c.Login == login, ct))
                return Result.Failure<CompletedLink>(new Failure(LocalAccountCodes.LoginTaken, "Another adult already signs in with this identifier.", null));
            db.LocalCredentials.Add(new LocalCredentialRow
            {
                PersonId = person.Id, Login = login, PasswordHash = passwordHash, PasswordChangedAt = now, CreatedAt = now,
            });
        }
        else
        {
            credential.PasswordHash = passwordHash;
            credential.PasswordChangedAt = now;
            credential.FailedCount = 0;
            credential.LockLevel = 0;
            credential.LockedUntil = null;
            // A reset ends every session of the adult, as « sign out everywhere » does (spec §8).
            person.SessionEpoch++;
        }
        link.UsedAt = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var purpose = credential is null ? CredentialLinkPurposes.Enroll : CredentialLinkPurposes.Reset;
        return Result.Success(new CompletedLink(PersonRegistry.ToInfo(person, true), purpose, link.Id));
    }

    public async Task<Result<PersonInfo>> CreateFirstAdminAsync(string displayName, string login, string passwordHash, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({PersonRegistry.ProvisioningLockKey})", ct);
        if (await db.Persons.AnyAsync(p => p.Kind == "adult", ct))
            return Result.Failure<PersonInfo>(new Failure(LocalAccountCodes.SetupClosed, "This instance is already set up.", null));
        var now = clock.GetCurrentInstant();
        var row = NewAdult(displayName, null, now);
        db.Persons.Add(row);
        if (!await db.HouseholdSettings.AnyAsync(ct))
            db.HouseholdSettings.Add(new HouseholdSettingsRow());
        db.LocalCredentials.Add(new LocalCredentialRow
        {
            PersonId = row.Id, Login = login, PasswordHash = passwordHash, PasswordChangedAt = now, CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Success(PersonRegistry.ToInfo(row, true));
    }

    public async Task<Result<PersonInfo>> CreateAdminAdultAsync(string displayName, string? email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return Result.Failure<PersonInfo>(new Failure(PersonCodes.NameRequired, "A name is required.", null));
        var address = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({PersonRegistry.ProvisioningLockKey})", ct);
        if (address is not null)
        {
            var normalized = address.ToLowerInvariant();
            if (await db.Persons.AnyAsync(p => p.Kind == "adult" && p.Email != null && p.Email.ToLower() == normalized, ct))
                return Result.Failure<PersonInfo>(new Failure(PersonCodes.EmailTaken, "Another adult already has this email address.", null));
        }
        var cap = await db.HouseholdSettings.Select(s => (int?)s.MaxAdults).FirstOrDefaultAsync(ct) ?? new HouseholdSettingsRow().MaxAdults;
        if (await db.Persons.CountAsync(p => p.Kind == "adult", ct) >= cap)
            return Result.Failure<PersonInfo>(new Failure(PersonCodes.AdultCap, $"This household is limited to {cap} adults.", null));
        var row = NewAdult(displayName.Trim(), address, clock.GetCurrentInstant());
        db.Persons.Add(row);
        if (!await db.HouseholdSettings.AnyAsync(ct))
            db.HouseholdSettings.Add(new HouseholdSettingsRow());
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Success(PersonRegistry.ToInfo(row, false));
    }

    public async Task<IReadOnlyList<LocalAccountState>> StatesAsync(Instant now, CancellationToken ct)
    {
        var credentials = await db.LocalCredentials.AsNoTracking().Select(c => new { c.PersonId, c.LockedUntil }).ToListAsync(ct);
        var links = await db.CredentialLinks.AsNoTracking()
            .Where(l => l.UsedAt == null && l.RevokedAt == null && l.ExpiresAt > now)
            .Select(l => new { l.PersonId, l.ExpiresAt }).ToListAsync(ct);
        return credentials.Select(c => c.PersonId).Union(links.Select(l => l.PersonId)).Select(id =>
        {
            var credential = credentials.FirstOrDefault(c => c.PersonId == id);
            var lockedUntil = credential?.LockedUntil is { } until && until > now ? until : (Instant?)null;
            return new LocalAccountState(new PersonId(id), credential is not null, lockedUntil, links.FirstOrDefault(l => l.PersonId == id)?.ExpiresAt);
        }).ToList();
    }

    /// <summary>The SHA-256 of the whole link, in lower-case hexadecimal: a 256-bit secret needs no slow hash.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static bool LooksLikeLink(string? token) => token is { Length: > 9 and < 100 } && token.StartsWith(LinkPrefix, StringComparison.Ordinal);

    private IQueryable<CredentialLinkRow> Live(PersonId person)
        => db.CredentialLinks.Where(l => l.PersonId == person.Value && l.UsedAt == null && l.RevokedAt == null);

    private static PersonRow NewAdult(string displayName, string? email, Instant now) => new()
    {
        Id = PersonId.New().Value, Kind = "adult", DisplayName = displayName, Email = email, IsAdmin = true, CreatedAt = now,
    };

    private static LocalCredential ToCredential(LocalCredentialRow row)
        => new(new PersonId(row.PersonId), row.Login, row.PasswordHash, row.FailedCount, row.LockLevel, row.LockedUntil);

    private static Result<CompletedLink> NotFound() => Result.Failure<CompletedLink>(new NotFoundFailure("link", "?"));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
