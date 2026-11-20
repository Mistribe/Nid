using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Banking.Infrastructure.Persistence;

/// <summary>Adults' applications on <see cref="BankingDbContext"/>; the private key is encrypted with Data Protection, as <see cref="ConnectionStore"/>'s session id.</summary>
public sealed class ApplicationStore(BankingDbContext db, IDataProtectionProvider protection, IClock clock, ILogger<ApplicationStore> log)
    : IApplicationStore
{
    private readonly IDataProtector _protector = protection.CreateProtector("Nid.Banking.ApplicationKey");

    public async Task<StoredApplication?> FindAsync(PersonId person, CancellationToken ct)
    {
        var row = await db.Applications.AsNoTracking().SingleOrDefaultAsync(a => a.PersonId == person.Value, ct);
        return row is null ? null : new StoredApplication(person, row.ApplicationId, row.KeyLoadedAt, Decrypt(row.PrivateKey) is not null);
    }

    public async Task<EnableBankingApp?> CredentialAsync(PersonId person, CancellationToken ct)
    {
        var row = await db.Applications.AsNoTracking().SingleOrDefaultAsync(a => a.PersonId == person.Value, ct);
        return row is not null && Decrypt(row.PrivateKey) is { } pem ? new EnableBankingApp(row.ApplicationId, pem) : null;
    }

    public async Task SaveAsync(PersonId person, string applicationId, string? privateKeyPem, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var row = await db.Applications.SingleOrDefaultAsync(a => a.PersonId == person.Value, ct);
        if (row is null)
        {
            ArgumentNullException.ThrowIfNull(privateKeyPem);
            db.Applications.Add(new ApplicationRow
            {
                PersonId = person.Value, ApplicationId = applicationId, PrivateKey = Encrypt(privateKeyPem), KeyLoadedAt = now, CreatedAt = now,
            });
        }
        else
        {
            row.ApplicationId = applicationId;
            if (privateKeyPem is not null)
            {
                row.PrivateKey = Encrypt(privateKeyPem);
                row.KeyLoadedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(PersonId person, CancellationToken ct)
    {
        if (await db.Applications.SingleOrDefaultAsync(a => a.PersonId == person.Value, ct) is not { } row)
            return;
        db.Applications.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PersonId>> OwnersAsync(CancellationToken ct)
        => (await db.Applications.AsNoTracking().Select(a => a.PersonId).ToListAsync(ct)).Select(id => new PersonId(id)).ToList();

    private byte[] Encrypt(string pem) => _protector.Protect(Encoding.UTF8.GetBytes(pem));

    private string? Decrypt(byte[] secret)
    {
        try
        {
            return Encoding.UTF8.GetString(_protector.Unprotect(secret));
        }
        catch (CryptographicException)
        {
            // The key ring was lost or rotated: the adult must load the key again.
            log.LogWarning("An application's private key cannot be decrypted; it must be loaded again.");
            return null;
        }
    }
}
