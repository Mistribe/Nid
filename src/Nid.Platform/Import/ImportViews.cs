using System.Text.Json;
using Nid.Platform.Contracts;

namespace Nid.Platform.Import;

/// <summary>What every session endpoint answers. It carries no item id: an import report names file paths only.</summary>
public sealed record ImportSessionView(
    Guid Id, string State, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, ImportReport? Report, ImportAuditView? Audit = null)
{
    public static ImportSessionView From(ImportSession session, ImportAuditView? audit = null) => new(
        session.Id,
        ImportStates.Name(session.State),
        session.CreatedAt.ToDateTimeOffset(),
        session.ExpiresAt.ToDateTimeOffset(),
        session.ReportJson is null ? null : ImportReportJson.Deserialize(session.ReportJson),
        audit);
}

public static class ImportReportJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(ImportReport report) => JsonSerializer.Serialize(report, Options);

    public static ImportReport Deserialize(string json) => JsonSerializer.Deserialize<ImportReport>(json, Options)!;
}
