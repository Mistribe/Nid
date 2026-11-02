using System.Text.Json;
using System.Text.Json.Serialization;
using Nid.Childcare.Application;

namespace Nid.Host.Api.Json;

/// <summary>The Childcare module's request and response bodies, for NidJsonContexts.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.Strict,
    Converters = [typeof(ItemIdJsonConverter), typeof(LocalDateJsonConverter)])]
[JsonSerializable(typeof(ChildcareDetail))]
[JsonSerializable(typeof(ChildcareList))]
[JsonSerializable(typeof(ChildcareEndpoints.CreateChildcareBody))]
[JsonSerializable(typeof(ChildcareEndpoints.EditChildcareBody))]
[JsonSerializable(typeof(ChildcareEndpoints.ReviseChildcareBody))]
[JsonSerializable(typeof(ChildcareEndpoints.GapsBody))]
[JsonSerializable(typeof(ChildcareEndpoints.CloseChildcareBody))]
[JsonSerializable(typeof(ChildcareStatementList))]
[JsonSerializable(typeof(ChildcareStatementView))]
[JsonSerializable(typeof(ChildcareEndpoints.StatementBody))]
[JsonSerializable(typeof(ChildcareEndpoints.VersionBody))]
internal sealed partial class ChildcareJsonContext : JsonSerializerContext;
