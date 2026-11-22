using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Nid.Ai.Application;
using Nid.Ai.Protocol.Keys;
using NodaTime;

namespace Nid.Ai.Infrastructure.Persistence;

/// <summary>Nid's signing keys towards workers (<c>docs/ai/README.md</c> §3.6): ECDSA P-256, the private key encrypted by
/// Data Protection like the bank keys. A lost key ring makes them unreadable: every worker is paired again.</summary>
public sealed class NidKeyStore(AiDbContext db, IDataProtectionProvider protection, IClock clock) : INidKeys
{
    private readonly IDataProtector _protector = protection.CreateProtector("Nid.Ai.Key");

    public async Task<NidKey> ActiveAsync(CancellationToken ct)
    {
        if (await db.Keys.AsNoTracking().SingleOrDefaultAsync(k => k.Status == "active", ct) is { } row)
            return Open(row);

        try
        {
            return await CreateAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another request created it first (one active key: a unique index).
            db.ChangeTracker.Clear();
            return Open(await db.Keys.AsNoTracking().SingleAsync(k => k.Status == "active", ct));
        }
    }

    public async Task<NidKey?> FindAsync(Guid id, CancellationToken ct)
        => await db.Keys.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id, ct) is { } row ? Open(row) : null;

    public async Task<NidKey> RotateAsync(CancellationToken ct)
    {
        await ActiveAsync(ct);
        await db.Keys.Where(k => k.Status == "active").ExecuteUpdateAsync(set => set.SetProperty(k => k.Status, "retiring"), ct);
        return await CreateAsync(ct);
    }

    public async Task RetireUnusedAsync(CancellationToken ct)
    {
        var unused = await db.Keys
            .Where(k => k.Status == "retiring" && !db.Workers.Any(w => w.NidKeyId == k.Id && w.Status != "revoked"))
            .ToListAsync(ct);
        db.Keys.RemoveRange(unused);
        await db.SaveChangesAsync(ct);
    }

    private async Task<NidKey> CreateAsync(CancellationToken ct)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var row = new AiKeyRow
        {
            Id = Guid.CreateVersion7(),
            PublicKey = publicKey,
            PrivateKey = _protector.Protect(key.ExportPkcs8PrivateKey()),
            Fingerprint = KeyFingerprint.Compact(publicKey),
            Status = "active",
            CreatedAt = clock.GetCurrentInstant(),
        };
        db.Keys.Add(row);
        await db.SaveChangesAsync(ct);
        return Open(row);
    }

    private NidKey Open(AiKeyRow row)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(_protector.Unprotect(row.PrivateKey), out _);
        return new NidKey(row.Id, key, row.Fingerprint, row.Status == "active", row.CreatedAt);
    }
}
