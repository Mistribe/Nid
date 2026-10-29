using Microsoft.EntityFrameworkCore;
using Nid.Platform.Infrastructure.Audit;

namespace Nid.TestSupport;

/// <summary>Reads the audit log back (1F3a spec §5): the security events a test expects to find recorded.</summary>
public static class AuditApi
{
    public static async Task<List<AuditEntryRow>> EventsAsync(NidApp app, string name)
    {
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        return await platform.AuditLog.Where(r => r.Category == "security" && r.Action == name).OrderBy(r => r.Seq).ToListAsync();
    }
}
