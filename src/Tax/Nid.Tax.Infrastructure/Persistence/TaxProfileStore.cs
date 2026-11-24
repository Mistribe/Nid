using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Tax.Application;
using Nid.Tax.Contracts;

namespace Nid.Tax.Infrastructure.Persistence;

/// <summary>Tax estimate spec §7: the profile as a camelCase JSON document, one row per revenue year.</summary>
public sealed partial class TaxProfileStore(TaxDbContext db) : ITaxProfileStore
{
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(TaxProfile))]
    private sealed partial class TaxProfileJson : JsonSerializerContext;

    public async Task<StoredTaxProfile?> FindAsync(int year, CancellationToken ct)
    {
        var row = await db.TaxProfiles.AsNoTracking().Where(p => p.Year <= year).OrderByDescending(p => p.Year).FirstOrDefaultAsync(ct);
        return row is null ? null : new StoredTaxProfile(row.Year, JsonSerializer.Deserialize(row.Data, TaxProfileJson.Default.TaxProfile) ?? TaxProfile.Empty);
    }

    public async Task SaveAsync(int year, TaxProfile profile, CancellationToken ct)
    {
        var row = await db.TaxProfiles.FirstOrDefaultAsync(p => p.Year == year, ct);
        if (row is null)
            db.TaxProfiles.Add(row = new TaxProfileRow { Year = year });
        row.Data = JsonSerializer.Serialize(profile, TaxProfileJson.Default.TaxProfile);
        await db.SaveChangesAsync(ct);
    }
}
