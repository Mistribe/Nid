using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Nid.Investments.Application;

namespace Nid.Investments.Infrastructure.Persistence;

/// <summary>#513: investments.settings, a single row moved from <c>core.household_settings</c>.</summary>
public sealed class SettingsRow
{
    public int Id { get; set; } = 1;

    /// <summary>Issue #209: the fee references as JSON (%, keys as Core wrote them); null until the household saved some.</summary>
    public string? FeeReferences { get; set; }
}

/// <summary>#513: Investments' settings row; its migration creates it, a missing one reads as no setting.</summary>
public sealed partial class SettingsStore(InvestmentsDbContext db) : IInvestmentsSettings
{
    // Default options, as Core's: the JSON copied from core.household_settings reads back as it was written.
    [JsonSerializable(typeof(FeeReferences))]
    private sealed partial class SettingsJson : JsonSerializerContext;

    public async Task<FeeReferences?> FeeReferencesAsync(CancellationToken ct)
    {
        var json = await db.Settings.AsNoTracking().Select(s => s.FeeReferences).FirstOrDefaultAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize(json, SettingsJson.Default.FeeReferences);
    }

    public async Task SaveFeeReferencesAsync(FeeReferences references, CancellationToken ct)
    {
        var row = await db.Settings.FirstOrDefaultAsync(ct);
        if (row is null)
            db.Settings.Add(row = new SettingsRow());
        row.FeeReferences = JsonSerializer.Serialize(references, SettingsJson.Default.FeeReferences);
        await db.SaveChangesAsync(ct);
    }
}
