using System.Text.Json;
using System.Text.Json.Serialization;
using Nid.Investments.Application;
using Nid.Investments.Application.Prices;

namespace Nid.Host.Api.Json;

/// <summary>The Investments module's request and response bodies, for NidJsonContexts.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.Strict,
    Converters = [typeof(ItemIdJsonConverter), typeof(LocalDateJsonConverter), typeof(OneOrManyJsonConverter<InvestmentEndpoints.SwitchLegBody>)])]
[JsonSerializable(typeof(PriceRunPage))]
[JsonSerializable(typeof(InvestmentEndpoints.AcceptPriceBody))]
[JsonSerializable(typeof(List<InvestmentEndpoints.SwitchLegBody>))]
[JsonSerializable(typeof(InvestmentEndpoints.BreakdownBody))]
[JsonSerializable(typeof(HoldingBreakdownView))]
[JsonSerializable(typeof(InvestmentEndpoints.CloseBody))]
[JsonSerializable(typeof(InvestmentEndpoints.CostReportBody))]
[JsonSerializable(typeof(InvestmentEndpoints.CreateEnvelopeBody))]
[JsonSerializable(typeof(InvestmentEndpoints.EditEnvelopeBody))]
[JsonSerializable(typeof(InvestmentEndpoints.FlowBody))]
[JsonSerializable(typeof(InvestmentEndpoints.HoldingBody))]
[JsonSerializable(typeof(InvestmentEndpoints.MovementBody))]
[JsonSerializable(typeof(InvestmentEndpoints.RoutineBody))]
[JsonSerializable(typeof(InvestmentEndpoints.RoutineUndoBody))]
[JsonSerializable(typeof(InvestmentEndpoints.SnapshotBody))]
[JsonSerializable(typeof(InvestmentEndpoints.SwitchBody))]
[JsonSerializable(typeof(InvestmentEndpoints.TransferBody))]
[JsonSerializable(typeof(InvestmentEndpoints.ValuationBody))]
[JsonSerializable(typeof(CostReportList))]
[JsonSerializable(typeof(CostReportView))]
[JsonSerializable(typeof(EnvelopeDetail))]
[JsonSerializable(typeof(EnvelopeList))]
[JsonSerializable(typeof(EnvelopeSeries))]
[JsonSerializable(typeof(HoldingMovementList))]
[JsonSerializable(typeof(HoldingSeries))]
[JsonSerializable(typeof(HoldingSnapshotList))]
[JsonSerializable(typeof(HoldingsView))]
[JsonSerializable(typeof(InvestmentsHoldings))]
[JsonSerializable(typeof(PassiveIncome))]
[JsonSerializable(typeof(InvestmentEndpoints.FeeReferencesBody))]
[JsonSerializable(typeof(InvestmentsOverview))]
[JsonSerializable(typeof(InvestmentsPerformance))]
[JsonSerializable(typeof(MovementRecorded))]
[JsonSerializable(typeof(PeriodView))]
[JsonSerializable(typeof(PriceSyncRequested))]
[JsonSerializable(typeof(RoutineRecap))]
[JsonSerializable(typeof(RoutineUndone))]
[JsonSerializable(typeof(RoutineView))]
[JsonSerializable(typeof(SnapshotRecorded))]
[JsonSerializable(typeof(SwitchRecorded))]
internal sealed partial class InvestmentsJsonContext : JsonSerializerContext;
