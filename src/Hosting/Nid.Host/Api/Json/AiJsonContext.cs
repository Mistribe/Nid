using System.Text.Json;
using System.Text.Json.Serialization;
using Nid.Ai.Contracts;

namespace Nid.Host.Api.Json;

/// <summary>The Ai module's request and response bodies, for NidJsonContexts.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.Strict,
    Converters = [typeof(ItemIdJsonConverter), typeof(LocalDateJsonConverter), typeof(InstantJsonConverter)])]
[JsonSerializable(typeof(WorkerList))]
[JsonSerializable(typeof(WorkerView))]
[JsonSerializable(typeof(RegisteredWorker))]
[JsonSerializable(typeof(NidKeyRotation))]
[JsonSerializable(typeof(AiEndpoints.RegisterWorkerBody))]
[JsonSerializable(typeof(AiEndpoints.PairWorkerBody))]
[JsonSerializable(typeof(AiEndpoints.RenameWorkerBody))]
public sealed partial class AiJsonContext : JsonSerializerContext;
