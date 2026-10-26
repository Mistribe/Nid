using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nid.Core.Contracts;
using Nid.Platform.Infrastructure.Persistence;
using Nid.Platform.Notifications;
using NodaTime;
using System.Text;

namespace Nid.Platform.Infrastructure.Notifications;

/// <summary>Discord webhooks on <see cref="PlatformDbContext"/>; the URL is a secret, encrypted with Data Protection.</summary>
public sealed class WebhookStore(PlatformDbContext db, IDataProtectionProvider protection, IClock clock, ILogger<WebhookStore> log) : IWebhookStore
{
    private readonly IDataProtector _protector = protection.CreateProtector("Nid.Notifications.Webhook");

    public async Task<StoredWebhook?> FindAsync(PersonId? owner, CancellationToken ct)
    {
        var id = owner?.Value;
        var row = await db.Webhooks.AsNoTracking().FirstOrDefaultAsync(w => w.PersonId == id, ct);
        if (row is null)
            return null;
        try
        {
            return new StoredWebhook(Encoding.UTF8.GetString(_protector.Unprotect(row.Url)), row.Status);
        }
        catch (CryptographicException)
        {
            // The key ring was lost or rotated: shown as broken so the page still loads and the webhook can be replaced.
            log.LogWarning("A {Owner} Discord webhook cannot be decrypted; treating it as broken.", owner is null ? "household" : "person");
            return new StoredWebhook("", WebhookStates.Broken);
        }
    }

    public async Task SetAsync(PersonId? owner, string url, CancellationToken ct)
    {
        var id = owner?.Value;
        var bytes = _protector.Protect(Encoding.UTF8.GetBytes(url));
        for (var attempt = 0; ; attempt++)
        {
            var row = await db.Webhooks.FirstOrDefaultAsync(w => w.PersonId == id, ct);
            if (row is null)
                db.Webhooks.Add(row = new NotificationWebhookRow { Id = Guid.NewGuid(), PersonId = id });
            row.Url = bytes;
            row.Status = WebhookStates.Ok;
            row.UpdatedAt = clock.GetCurrentInstant();
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException) when (attempt == 0 && row is { } r && db.Entry(r).State == EntityState.Added)
            {
                // A concurrent first insert won the unique index: reload and update, last write wins.
                db.Entry(r).State = EntityState.Detached;
            }
        }
    }

    public async Task RemoveAsync(PersonId? owner, CancellationToken ct)
    {
        var id = owner?.Value;
        if (await db.Webhooks.FirstOrDefaultAsync(w => w.PersonId == id, ct) is { } row)
        {
            db.Webhooks.Remove(row);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task MarkBrokenAsync(PersonId? owner, CancellationToken ct)
    {
        var id = owner?.Value;
        if (await db.Webhooks.FirstOrDefaultAsync(w => w.PersonId == id, ct) is { } row && row.Status != WebhookStates.Broken)
        {
            row.Status = WebhookStates.Broken;
            row.UpdatedAt = clock.GetCurrentInstant();
            await db.SaveChangesAsync(ct);
        }
    }
}
