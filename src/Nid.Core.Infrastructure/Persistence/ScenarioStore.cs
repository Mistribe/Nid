using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using NodaTime;
using NodaTime.Text;

namespace Nid.Core.Infrastructure.Persistence;

/// <summary>Property purchase scenario spec §5: the document as a camelCase JSON row keyed by its registry item.</summary>
public sealed partial class ScenarioStore(CoreDbContext db) : IScenarioStore
{
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, Converters =
    [
        typeof(CamelCaseEnum<PurchaseUse>), typeof(CamelCaseEnum<DeferralKind>), typeof(CamelCaseEnum<ScenarioInsuranceKind>),
        typeof(ItemIdConverter), typeof(LocalDateConverter),
    ])]
    [JsonSerializable(typeof(PropertyPurchase))]
    private sealed partial class ScenarioJson : JsonSerializerContext;

    /// <summary>An enum as its camelCase name; one per enum of the document.</summary>
    private sealed class CamelCaseEnum<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase) where TEnum : struct, Enum;

    private sealed class ItemIdConverter : JsonConverter<ItemId>
    {
        public override ItemId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetGuid());
        public override void Write(Utf8JsonWriter writer, ItemId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    }

    private sealed class LocalDateConverter : JsonConverter<LocalDate>
    {
        public override LocalDate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => LocalDatePattern.Iso.Parse(reader.GetString() ?? "").Value;
        public override void Write(Utf8JsonWriter writer, LocalDate value, JsonSerializerOptions options)
            => writer.WriteStringValue(LocalDatePattern.Iso.Format(value));
    }

    public async Task<StoredScenario?> FindAsync(ItemId id, CancellationToken ct)
    {
        var row = await db.Scenarios.AsNoTracking().FirstOrDefaultAsync(s => s.ItemId == id.Value, ct);
        return row is null ? null : ToStored(row);
    }

    public async Task<IReadOnlyList<StoredScenario>> FindManyAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct)
    {
        var guids = ids.Select(i => i.Value).ToList();
        var rows = await db.Scenarios.AsNoTracking().Where(s => guids.Contains(s.ItemId)).ToListAsync(ct);
        return rows.Select(ToStored).ToList();
    }

    public async Task SaveAsync(StoredScenario scenario, CancellationToken ct)
    {
        var row = await db.Scenarios.FirstOrDefaultAsync(s => s.ItemId == scenario.Item.Value, ct);
        if (row is null)
            db.Scenarios.Add(row = new ScenarioRow { ItemId = scenario.Item.Value });
        row.Kind = scenario.Kind;
        row.Scope = scenario.Scope == VisibilityScope.Me ? "me" : "household";
        row.Data = JsonSerializer.Serialize(scenario.Document, ScenarioJson.Default.PropertyPurchase);
        await db.SaveChangesAsync(ct);
    }

    private static StoredScenario ToStored(ScenarioRow row) => new(
        new ItemId(row.ItemId), row.Kind, row.Scope == "me" ? VisibilityScope.Me : VisibilityScope.Household,
        JsonSerializer.Deserialize(row.Data, ScenarioJson.Default.PropertyPurchase)!);
}
