using System.Text.Json;
using System.Text.Json.Serialization;
using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Platform.Import;

/// <summary>What every session endpoint answers. It carries no item id: an import report names file paths only.</summary>
public sealed record ImportSessionView(
    Guid Id, string State, Instant CreatedAt, Instant ExpiresAt, ImportReport? Report, ImportAuditView? Audit = null)
{
    public static ImportSessionView From(ImportSession session, ImportAuditView? audit = null) => new(
        session.Id,
        ImportStates.Name(session.State),
        session.CreatedAt,
        session.ExpiresAt,
        session.ReportJson is null ? null : ImportReportJson.Deserialize(session.ReportJson),
        audit);
}

public static partial class ImportReportJson
{
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(ImportReport))]
    private sealed partial class ReportJson : JsonSerializerContext;

    public static string Serialize(ImportReport report) => JsonSerializer.Serialize(report, ReportJson.Default.ImportReport);

    public static ImportReport Deserialize(string json) => JsonSerializer.Deserialize(json, ReportJson.Default.ImportReport)!;
}
