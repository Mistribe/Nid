using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;

namespace Nid.Core.Infrastructure.Persistence;

/// <summary>Tax estimate spec §7: the profile as a camelCase JSON document, one row per revenue year.</summary>
public sealed class TaxProfileStore(CoreDbContext db) : ITaxProfileStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<StoredTaxProfile?> FindAsync(int year, CancellationToken ct)
    {
        var row = await db.TaxProfiles.AsNoTracking().Where(p => p.Year <= year).OrderByDescending(p => p.Year).FirstOrDefaultAsync(ct);
        return row is null ? null : new StoredTaxProfile(row.Year, JsonSerializer.Deserialize<TaxProfile>(row.Data, Json) ?? TaxProfile.Empty);
    }

    public async Task SaveAsync(int year, TaxProfile profile, CancellationToken ct)
    {
        var row = await db.TaxProfiles.FirstOrDefaultAsync(p => p.Year == year, ct);
        if (row is null)
            db.TaxProfiles.Add(row = new TaxProfileRow { Year = year });
        row.Data = JsonSerializer.Serialize(profile, Json);
        await db.SaveChangesAsync(ct);
    }
}
