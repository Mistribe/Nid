using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Nid.Ai.Application;
using NodaTime;

namespace Nid.Ai.Infrastructure.Persistence;

/// <summary>The cloud settings and the providers' keys (§10): a key is encrypted by Data Protection and leaves the store only
/// in clear for a call, through <see cref="KeyAsync"/>.</summary>
public sealed class CloudSettingsStore(AiDbContext db, IDataProtectionProvider protection) : ICloudSettings
{
    private readonly IDataProtector _protector = protection.CreateProtector("Nid.Ai.ProviderKey");

    public async Task<string?> RegionAsync(CancellationToken ct)
        => await db.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.Region).SingleOrDefaultAsync(ct);

    public async Task SetRegionAsync(string region, CancellationToken ct)
    {
        var settings = await db.Settings.SingleOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is null)
            db.Settings.Add(settings = new AiSettingsRow { Id = 1 });
        settings.Region = region;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<EnabledProfile>> EnabledAsync(CancellationToken ct)
        => await db.CloudProfiles.AsNoTracking().Select(p => new EnabledProfile(p.ProfileId, p.AcceptedReviewedOn)).ToListAsync(ct);

    public async Task EnableAsync(string profileId, LocalDate acceptedReviewedOn, Guid person, Instant at, CancellationToken ct)
    {
        var row = await db.CloudProfiles.SingleOrDefaultAsync(p => p.ProfileId == profileId, ct);
        if (row is null)
            db.CloudProfiles.Add(row = new CloudProfileRow { ProfileId = profileId });
        (row.AcceptedReviewedOn, row.EnabledByPersonId, row.EnabledAt) = (acceptedReviewedOn, person, at);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DisableAsync(string profileId, CancellationToken ct)
    {
        if (await db.CloudProfiles.SingleOrDefaultAsync(p => p.ProfileId == profileId, ct) is not { } row)
            return false;
        db.CloudProfiles.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyDictionary<string, string>> KeyHintsAsync(CancellationToken ct)
        => await db.Credentials.AsNoTracking().Where(c => c.RevokedAt == null).ToDictionaryAsync(c => c.Provider, c => c.Hint, ct);

    public async Task SetKeyAsync(string provider, string key, Guid person, Instant at, CancellationToken ct)
    {
        foreach (var active in await db.Credentials.Where(c => c.Provider == provider && c.RevokedAt == null).ToListAsync(ct))
            active.RevokedAt = at;
        await db.SaveChangesAsync(ct); // the filtered unique index: the old key is revoked before the new one exists
        db.Credentials.Add(new LlmCredentialRow
        {
            Id = Guid.CreateVersion7(), Provider = provider, SecretEncrypted = _protector.Protect(Encoding.UTF8.GetBytes(key)),
            Hint = key[^4..], CreatedByPersonId = person, CreatedAt = at,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> RevokeKeyAsync(string provider, Instant at, CancellationToken ct)
    {
        var active = await db.Credentials.Where(c => c.Provider == provider && c.RevokedAt == null).ToListAsync(ct);
        foreach (var credential in active)
            credential.RevokedAt = at;
        await db.SaveChangesAsync(ct);
        return active.Count > 0;
    }

    public async Task<string?> KeyAsync(string provider, Instant usedAt, CancellationToken ct)
    {
        var credential = await db.Credentials.SingleOrDefaultAsync(c => c.Provider == provider && c.RevokedAt == null, ct);
        if (credential is null)
            return null;
        // last_used_at without an audit row for each call: one statement, no tracked change.
        await db.Credentials.Where(c => c.Id == credential.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUsedAt, usedAt), ct);
        return Encoding.UTF8.GetString(_protector.Unprotect(credential.SecretEncrypted));
    }
}
