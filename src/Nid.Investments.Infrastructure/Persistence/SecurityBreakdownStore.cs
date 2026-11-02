using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Investments.Infrastructure.Persistence;

public sealed partial class SecurityBreakdownStore(InvestmentsDbContext db) : ISecurityBreakdownStore
{
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(List<BreakdownPart>))]
    private sealed partial class BreakdownJson : JsonSerializerContext;

    public async Task<IReadOnlyDictionary<string, SecurityBreakdown>> LatestAsync(IReadOnlyCollection<string> isins, CancellationToken ct)
        => (await db.SecurityBreakdowns.AsNoTracking().Where(b => isins.Contains(b.Isin)).ToListAsync(ct))
            .GroupBy(b => b.Isin).ToDictionary(g => g.Key, g => ToBreakdown(g.MaxBy(b => b.AsOf)!));

    public async Task<IReadOnlyList<SecurityBreakdown>> AllAsync(IReadOnlyCollection<string> isins, CancellationToken ct)
        => (await db.SecurityBreakdowns.AsNoTracking().Where(b => isins.Contains(b.Isin)).OrderBy(b => b.AsOf).ToListAsync(ct))
            .Select(ToBreakdown).ToList();

    public async Task<Guid> WriteAsync(SecurityBreakdown breakdown, CancellationToken ct)
    {
        var row = await db.SecurityBreakdowns.SingleOrDefaultAsync(b => b.Isin == breakdown.Isin && b.AsOf == breakdown.AsOf, ct);
        if (row is null)
            db.SecurityBreakdowns.Add(row = new SecurityBreakdownRow { Id = breakdown.Id, Isin = breakdown.Isin, AsOf = breakdown.AsOf });
        row.Regions = JsonSerializer.Serialize(breakdown.Regions.ToList(), BreakdownJson.Default.ListBreakdownPart);
        row.Sectors = breakdown.Sectors is null ? null : JsonSerializer.Serialize(breakdown.Sectors.ToList(), BreakdownJson.Default.ListBreakdownPart);
        row.DocumentId = breakdown.DocumentId;
        row.RecordedBy = breakdown.RecordedBy.Value;
        row.RecordedAt = breakdown.RecordedAt;
        await db.SaveChangesAsync(ct);
        return row.Id;
    }

    public async Task<bool> DeleteAsync(string isin, LocalDate asOf, CancellationToken ct)
        => await db.SecurityBreakdowns.Where(b => b.Isin == isin && b.AsOf == asOf).ExecuteDeleteAsync(ct) > 0;

    // Through the change tracker, like Core's stamp; security_breakdown is excluded from the item audit, so nothing is recorded.
    public async Task StampDocumentAsync(Guid documentId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        foreach (var row in await db.SecurityBreakdowns.Where(b => ids.Contains(b.Id)).ToListAsync(ct))
            row.DocumentId = documentId;
        await db.SaveChangesAsync(ct);
    }

    private static SecurityBreakdown ToBreakdown(SecurityBreakdownRow r)
        => new(r.Id, r.Isin, r.AsOf, JsonSerializer.Deserialize(r.Regions, BreakdownJson.Default.ListBreakdownPart)!,
            r.Sectors is null ? null : JsonSerializer.Deserialize(r.Sectors, BreakdownJson.Default.ListBreakdownPart),
            r.DocumentId, new PersonId(r.RecordedBy), r.RecordedAt);
}
