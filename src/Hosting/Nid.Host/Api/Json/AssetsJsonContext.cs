using System.Text.Json;
using System.Text.Json.Serialization;
using Nid.Assets.Application;

namespace Nid.Host.Api.Json;

/// <summary>The Assets module's request and response bodies, for NidJsonContexts.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.Strict,
    Converters = [typeof(ItemIdJsonConverter), typeof(LocalDateJsonConverter)])]
[JsonSerializable(typeof(AssetView))]
[JsonSerializable(typeof(AssetListView))]
[JsonSerializable(typeof(AssetEndpoints.CreateAssetBody))]
[JsonSerializable(typeof(AssetEndpoints.EditAssetBody))]
[JsonSerializable(typeof(AssetEndpoints.AssetValuationBody))]
internal sealed partial class AssetsJsonContext : JsonSerializerContext;
