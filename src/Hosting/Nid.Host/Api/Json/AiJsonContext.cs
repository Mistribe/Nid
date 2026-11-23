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
[JsonSerializable(typeof(WorkerTestResult))]
[JsonSerializable(typeof(AiSettingsView))]
[JsonSerializable(typeof(ModelCapabilities))]
[JsonSerializable(typeof(ModelJobPage))]
[JsonSerializable(typeof(DeclaredModels))]
[JsonSerializable(typeof(AiEndpoints.SafeBody))]
[JsonSerializable(typeof(AiEndpoints.RemoteWorkersBody))]
[JsonSerializable(typeof(AiEndpoints.UseModeBody))]
[JsonSerializable(typeof(AiEndpoints.UseModelBody))]
[JsonSerializable(typeof(AiEndpoints.CloudRegionBody))]
[JsonSerializable(typeof(AiEndpoints.EnableProfileBody))]
[JsonSerializable(typeof(AiEndpoints.ProviderKeyBody))]
[JsonSerializable(typeof(CloudSettingsView))]
[JsonSerializable(typeof(AiEndpoints.AiBudgetBody))]
[JsonSerializable(typeof(AiBudgetView))]
[JsonSerializable(typeof(LlmUsageView))]
[JsonSerializable(typeof(Nid.Ai.Contracts.ModelJobView))]
[JsonSerializable(typeof(AiEndpoints.RegisterWorkerBody))]
[JsonSerializable(typeof(AiEndpoints.PairWorkerBody))]
[JsonSerializable(typeof(AiEndpoints.RenameWorkerBody))]
[JsonSerializable(typeof(AssistantEndpoints.AssistantTurnBody))]
[JsonSerializable(typeof(AssistantEndpoints.ConversationRetentionBody))]
[JsonSerializable(typeof(AssistantEndpoints.AssistantEnabledBody))]
[JsonSerializable(typeof(AssistantEndpoints.AssistantOffsiteBody))]
[JsonSerializable(typeof(AssistantMeView))]
[JsonSerializable(typeof(ConversationPage))]
[JsonSerializable(typeof(ConversationView))]
[JsonSerializable(typeof(AssistantEndpoints.AssistantTextEvent))]
[JsonSerializable(typeof(AssistantEndpoints.AssistantToolEvent))]
[JsonSerializable(typeof(AssistantEndpoints.AssistantHeldBackEvent))]
[JsonSerializable(typeof(AssistantEndpoints.AssistantDoneEvent))]
[JsonSerializable(typeof(AssistantEndpoints.AssistantErrorEvent))]
public sealed partial class AiJsonContext : JsonSerializerContext;
