using System.Text.Json;
using System.Text.Json.Serialization;
using Nid.Documents.Application;

namespace Nid.Host.Api.Json;

/// <summary>The Documents module's request and response bodies, for NidJsonContexts.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.Strict,
    Converters = [typeof(ItemIdJsonConverter), typeof(LocalDateJsonConverter), typeof(InstantJsonConverter)])]
[JsonSerializable(typeof(DocumentDetail))]
[JsonSerializable(typeof(DocumentList))]
[JsonSerializable(typeof(DocumentSummary))]
[JsonSerializable(typeof(LinkAdded))]
[JsonSerializable(typeof(DocumentTargetList))]
[JsonSerializable(typeof(LinkPreview))]
[JsonSerializable(typeof(UploadedDocument))]
[JsonSerializable(typeof(FiledDocuments))]
[JsonSerializable(typeof(DocumentEndpoints.EditDocumentBody))]
[JsonSerializable(typeof(DocumentEndpoints.FileDocumentsBody))]
[JsonSerializable(typeof(DocumentEndpoints.LinkDocumentBody))]
[JsonSerializable(typeof(DocumentEndpoints.SetLinkRoleBody))]
internal sealed partial class DocumentsJsonContext : JsonSerializerContext;
