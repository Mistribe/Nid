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

/// <summary>Connections on <see cref="BankingDbContext"/>; the session id is a secret, encrypted with Data Protection as a Discord webhook.</summary>
public sealed class ConnectionStore(BankingDbContext db, IDataProtectionProvider protection, IClock clock, ILogger<ConnectionStore> log)
    : IConnectionStore
{
    private const string FileSource = "file";

    private readonly IDataProtector _protector = protection.CreateProtector("Nid.Banking.Session");

    public async Task<Guid> CreatePendingAsync(PersonId person, string aspspName, string country, byte[] stateHash, Instant expiresAt, CancellationToken ct)
    {
        var row = new ConnectionRow
        {
            Id = Guid.CreateVersion7(), PersonId = person.Value, AspspName = aspspName, AspspCountry = country, StateHash = stateHash,
            StateExpiresAt = expiresAt, CreatedAt = clock.GetCurrentInstant(),
        };
        db.Connections.Add(row);
        await db.SaveChangesAsync(ct);
        return row.Id;
    }

    public async Task<BankConnection?> FindByStateAsync(byte[] stateHash, CancellationToken ct)
    {
        var row = await db.Connections.SingleOrDefaultAsync(c => c.StateHash == stateHash, ct);
        if (row is null)
            return null;

        var live = row.StateExpiresAt > clock.GetCurrentInstant();
        row.StateHash = null;
        row.StateExpiresAt = null;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(row).State = EntityState.Detached;
            return null; // a concurrent callback used the state first
        }

        return live ? ToConnection(row) : null;
    }

    public Task SetStateAsync(Guid id, byte[] stateHash, Instant expiresAt, CancellationToken ct)
        => UpdateAsync(id, row =>
        {
            row.StateHash = stateHash;
            row.StateExpiresAt = expiresAt;
        }, ct);

    public async Task DeletePendingAsync(PersonId person, Instant before, CancellationToken ct)
    {
        // Loaded, as DeleteAsync, so the log records each one removed.
        db.Connections.RemoveRange(await db.Connections
            .Where(c => c.PersonId == person.Value && c.Status == "pending" && c.CreatedAt < before).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }

    public Task ActivateAsync(Guid id, string sessionId, Instant validUntil, CancellationToken ct)
        => UpdateAsync(id, row =>
        {
            row.SessionId = _protector.Protect(Encoding.UTF8.GetBytes(sessionId));
            row.ValidUntil = validUntil;
            row.Status = "active";
            row.LastErrorCode = null;
            row.ConsecutiveFailures = 0;
        }, ct);

    public async Task<IReadOnlyList<BankConnection>> MineAsync(PersonId person, CancellationToken ct)
        => (await db.Connections.AsNoTracking().Where(c => c.PersonId == person.Value && c.Source != FileSource).OrderBy(c => c.CreatedAt).ToListAsync(ct))
            .Select(ToConnection).ToList();

    public async Task<IReadOnlyList<BankConnection>> ActiveAsync(CancellationToken ct)
        => (await db.Connections.AsNoTracking().Where(c => (c.Status == "active" || c.Status == "error") && c.Source != FileSource)
                .OrderBy(c => c.CreatedAt).ToListAsync(ct))
            .Select(ToConnection).ToList();

    public async Task<Guid> FileConnectionAsync(PersonId person, CancellationToken ct)
    {
        // One file import per person at a time, until the caller's transaction ends: a double click finds the first one's account.
        await db.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock(hashtextextended({"file:" + person.Value}, 0))", ct);
        if (await db.Connections.Where(c => c.PersonId == person.Value && c.Source == FileSource).Select(c => (Guid?)c.Id).SingleOrDefaultAsync(ct) is { } id)
            return id;
        var row = new ConnectionRow { Id = Guid.CreateVersion7(), PersonId = person.Value, Source = FileSource, Status = "active", CreatedAt = clock.GetCurrentInstant() };
        db.Connections.Add(row);
        await db.SaveChangesAsync(ct);
        return row.Id;
    }

    public async Task<string?> SessionIdAsync(Guid id, CancellationToken ct)
    {
        var secret = await db.Connections.AsNoTracking().Where(c => c.Id == id).Select(c => c.SessionId).SingleOrDefaultAsync(ct);
        if (secret is null)
            return null;
        try
        {
            return Encoding.UTF8.GetString(_protector.Unprotect(secret));
        }
        catch (CryptographicException)
        {
            // The key ring was lost or rotated: the connection must be renewed.
            log.LogWarning("A bank session cannot be decrypted; the connection needs renewing.");
            return null;
        }
    }

    public Task SetStatusAsync(Guid id, ConnectionStatus status, string? errorCode, CancellationToken ct)
        => UpdateAsync(id, row =>
        {
            row.Status = Wire(status);
            row.LastErrorCode = errorCode;
        }, ct);

    public Task RecordSyncAsync(Guid id, Instant at, bool failed, CancellationToken ct)
        => UpdateAsync(id, row =>
        {
            if (failed)
            {
                row.ConsecutiveFailures++;
                return;
            }

            row.LastSyncedAt = at;
            row.ConsecutiveFailures = 0;
            if (row.Status == "error")
            {
                row.Status = "active";
                row.LastErrorCode = null;
            }
        }, ct);

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        if (await db.Connections.SingleOrDefaultAsync(c => c.Id == id, ct) is not { } row)
            return;

        // Loaded so the log records each source removed, rather than leaving them to the database's cascade.
        db.Sources.RemoveRange(await db.Sources.Where(s => s.ConnectionId == id).ToListAsync(ct));
        db.Connections.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    private async Task UpdateAsync(Guid id, Action<ConnectionRow> change, CancellationToken ct)
    {
        if (await db.Connections.SingleOrDefaultAsync(c => c.Id == id, ct) is not { } row)
            return;
        change(row);
        await db.SaveChangesAsync(ct);
    }

    private static string Wire(ConnectionStatus status) => status.ToString().ToLowerInvariant();

    private static BankConnection ToConnection(ConnectionRow r)
        => new(r.Id, new PersonId(r.PersonId), r.Source, r.AspspName, r.AspspCountry, Enum.Parse<ConnectionStatus>(r.Status, ignoreCase: true),
            r.ValidUntil, r.LastSyncedAt, r.LastErrorCode, r.ConsecutiveFailures, r.CreatedAt);
}
