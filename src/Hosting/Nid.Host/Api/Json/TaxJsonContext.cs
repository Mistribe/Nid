using System.Text.Json;
using System.Text.Json.Serialization;
using Nid.Core.Contracts;
using Nid.Tax.Application;

namespace Nid.Host.Api.Json;

/// <summary>The Tax module's request and response bodies, for NidJsonContexts.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.Strict,
    Converters = [typeof(ItemIdJsonConverter), typeof(LocalDateJsonConverter), typeof(InstantJsonConverter)])]
[JsonSerializable(typeof(Rental2044View))]
[JsonSerializable(typeof(SaleTaxView))]
[JsonSerializable(typeof(TaxEstimateView))]
[JsonSerializable(typeof(TaxChangesView))]
[JsonSerializable(typeof(TaxHistoryView))]
[JsonSerializable(typeof(TaxInputsView))]
[JsonSerializable(typeof(TaxResultView))]
[JsonSerializable(typeof(TaxEndpoints.TaxProfileBody))]
[JsonSerializable(typeof(TaxEndpoints.AddTaxAssessmentBody))]
[JsonSerializable(typeof(TaxEndpoints.CorrectTaxAssessmentBody))]
[JsonSerializable(typeof(TaxAssessmentView))]
[JsonSerializable(typeof(TaxAssessmentList))]
[JsonSerializable(typeof(SalaryAllowanceView))]
internal sealed partial class TaxJsonContext : JsonSerializerContext;
