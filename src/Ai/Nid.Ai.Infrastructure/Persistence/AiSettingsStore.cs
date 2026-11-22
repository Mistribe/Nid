using Microsoft.EntityFrameworkCore;
using Nid.Ai.Application;
using NodaTime;

namespace Nid.Ai.Infrastructure.Persistence;

public sealed class AiSettingsStore(AiDbContext db) : IAiSettings
{
    public async Task<bool> RemoteWorkersAllowedAsync(CancellationToken ct)
        => await db.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.RemoteWorkersAllowed).SingleOrDefaultAsync(ct);

    public async Task SetRemoteWorkersAllowedAsync(bool allowed, Guid person, Instant at, CancellationToken ct)
    {
        var consent = new ConsentRow { Id = Guid.CreateVersion7(), PersonId = person, Subject = "remote_workers", Value = allowed, At = at };
        db.Consents.Add(consent);
        var settings = await db.Settings.SingleOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is null)
            db.Settings.Add(settings = new AiSettingsRow { Id = 1 });
        settings.RemoteWorkersAllowed = allowed;
        settings.RemoteConsentId = consent.Id;
        await db.SaveChangesAsync(ct);
    }
}
