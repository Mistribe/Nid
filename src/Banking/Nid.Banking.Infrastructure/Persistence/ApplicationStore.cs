using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Banking.Infrastructure.Persistence;

/// <summary>Adults' applications on <see cref="BankingDbContext"/>; the private key is encrypted with Data Protection, as <see cref="ConnectionStore"/>'s session id.</summary>
public sealed partial class ApplicationStore(BankingDbContext db, IDataProtectionProvider protection, IClock clock, ILogger<ApplicationStore> log)
    : IApplicationStore
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "An application's private key cannot be decrypted; it must be loaded again.")]
    private partial void LogKeyUndecryptable();

    private readonly IDataProtector _protector = protection.CreateProtector("Nid.Banking.ApplicationKey");

    public async Task<StoredApplication?> FindAsync(PersonId person, CancellationToken ct)
    {
        var row = await db.Applications.AsNoTracking().SingleOrDefaultAsync(a => a.PersonId == person.Value, ct);
        return row is null ? null : new StoredApplication(person, row.ApplicationId, row.KeyLoadedAt, Decrypt(row.PrivateKey, quiet: true) is not null);
    }

    public async Task<EnableBankingApp?> CredentialAsync(PersonId person, CancellationToken ct)
    {
        var row = await db.Applications.AsNoTracking().SingleOrDefaultAsync(a => a.PersonId == person.Value, ct);
        return row is not null && Decrypt(row.PrivateKey) is { } pem ? new EnableBankingApp(row.ApplicationId, pem) : null;
    }

    public async Task<bool> SaveAsync(PersonId person, string applicationId, string? privateKeyPem, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var row = await db.Applications.SingleOrDefaultAsync(a => a.PersonId == person.Value, ct);
        if (row is null)
        {
            if (privateKeyPem is null)
                return false; // the application was deleted since the caller read it
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

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception e) when (e is DbUpdateConcurrencyException
            || (e is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } }))
        {
            // A concurrent save of the same person: a duplicate key, or a changed row version.
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<bool> DeleteAsync(PersonId person, CancellationToken ct)
    {
        if (await db.Applications.SingleOrDefaultAsync(a => a.PersonId == person.Value, ct) is not { } row)
            return true;
        db.Applications.Remove(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear(); // changed or removed meanwhile
            return false;
        }
    }

    public async Task<IReadOnlyList<PersonId>> OwnersAsync(CancellationToken ct)
        => (await db.Applications.AsNoTracking().Select(a => a.PersonId).ToListAsync(ct)).Select(id => new PersonId(id)).ToList();

    private byte[] Encrypt(string pem) => _protector.Protect(Encoding.UTF8.GetBytes(pem));

    private string? Decrypt(byte[] secret, bool quiet = false)
    {
        try
        {
            return Encoding.UTF8.GetString(_protector.Unprotect(secret));
        }
        catch (CryptographicException)
        {
            // The key ring was lost or rotated: the adult must load the key again.
            if (!quiet)
                LogKeyUndecryptable();
            return null;
        }
    }
}
