using System.Text.Json;
using System.Text.Json.Serialization;
using Nid.Income.Application;

namespace Nid.Host.Api.Json;

/// <summary>The Income module's request and response bodies, for NidJsonContexts.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.Strict,
    Converters = [typeof(ItemIdJsonConverter), typeof(LocalDateJsonConverter)])]
[JsonSerializable(typeof(IncomeEndpoints.CloseIncomeSourceBody))]
[JsonSerializable(typeof(IncomeEndpoints.CreateIncomeSourceBody))]
[JsonSerializable(typeof(IncomeEndpoints.EditIncomeSourceBody))]
[JsonSerializable(typeof(IncomeEndpoints.IncomeOneOffBody))]
[JsonSerializable(typeof(IncomeEndpoints.IncomePhaseBody))]
[JsonSerializable(typeof(IncomeEndpoints.IncomePhaseChangeBody))]
[JsonSerializable(typeof(IncomeEndpoints.IncomePhaseCorrectionBody))]
[JsonSerializable(typeof(IncomeExpected))]
[JsonSerializable(typeof(IncomeOverview))]
[JsonSerializable(typeof(IncomeSourceDetail))]
[JsonSerializable(typeof(IncomeSourceList))]
[JsonSerializable(typeof(IncomeSourceOverview))]
internal sealed partial class IncomeJsonContext : JsonSerializerContext;
