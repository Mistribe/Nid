using System.Text.Json;
using System.Text.Json.Serialization;
using Nid.Goals.Application;

namespace Nid.Host.Api.Json;

/// <summary>The Goals module's request and response bodies, for NidJsonContexts.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.Strict,
    Converters = [typeof(ItemIdJsonConverter), typeof(LocalDateJsonConverter)])]
[JsonSerializable(typeof(GoalDetail))]
[JsonSerializable(typeof(GoalList))]
[JsonSerializable(typeof(GoalEndpoints.CreateGoalBody))]
[JsonSerializable(typeof(GoalEndpoints.EditGoalBody))]
[JsonSerializable(typeof(GoalEndpoints.GoalProgressBody))]
internal sealed partial class GoalsJsonContext : JsonSerializerContext;
