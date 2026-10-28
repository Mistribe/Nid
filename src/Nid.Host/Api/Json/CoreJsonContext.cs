using System.Text.Json;
using System.Text.Json.Serialization;
using Nid.Core.Application;
using Nid.Core.Application.Admin;
using Nid.Core.Application.Dashboard;
using Nid.Core.Application.Fx;
using Nid.Core.Application.Scenarios;
using Nid.Core.Application.Tax;
using Nid.Core.Contracts;
using Nid.Host.Auth;

namespace Nid.Host.Api.Json;

/// <summary>The Core module's request and response bodies and MCP answers, for NidJsonContexts.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.Strict,
    Converters = [typeof(ItemIdJsonConverter), typeof(LocalDateJsonConverter)])]
[JsonSerializable(typeof(IEnumerable<PersonEndpoints.PersonDto>))]
[JsonSerializable(typeof(List<PersonEndpoints.PersonDto>))]
[JsonSerializable(typeof(IReadOnlyList<AdultView>))]
[JsonSerializable(typeof(IReadOnlyList<FxRateView>))]
[JsonSerializable(typeof(IReadOnlyList<FxSyncRunView>))]
[JsonSerializable(typeof(IReadOnlyList<TokenView>))]
[JsonSerializable(typeof(AdultView))]
[JsonSerializable(typeof(HouseholdSettingsView))]
[JsonSerializable(typeof(CreatedTokenView))]
[JsonSerializable(typeof(DashboardView))]
[JsonSerializable(typeof(NetWorthHistoryView))]
[JsonSerializable(typeof(FxRateView))]
[JsonSerializable(typeof(FxSyncRequested))]
[JsonSerializable(typeof(PrivateItemsView))]
[JsonSerializable(typeof(ProposalDetail))]
[JsonSerializable(typeof(ProposalList))]
[JsonSerializable(typeof(ProposalView))]
[JsonSerializable(typeof(ScenarioDefaultsView))]
[JsonSerializable(typeof(ScenarioList))]
[JsonSerializable(typeof(ScenarioResultView))]
[JsonSerializable(typeof(ScenarioView))]
[JsonSerializable(typeof(Rental2044View))]
[JsonSerializable(typeof(SaleTaxView))]
[JsonSerializable(typeof(TaxEstimateView))]
[JsonSerializable(typeof(TaxInputsView))]
[JsonSerializable(typeof(TaxResultView))]
[JsonSerializable(typeof(Preferences))]
[JsonSerializable(typeof(AdminEndpoints.HouseholdBody))]
[JsonSerializable(typeof(AdminEndpoints.PersonIdResponse))]
[JsonSerializable(typeof(AdminEndpoints.PreRegisterBody))]
[JsonSerializable(typeof(DashboardEndpoints.AllocationTargetBody))]
[JsonSerializable(typeof(FxEndpoints.ManualRateBody))]
[JsonSerializable(typeof(NewTokenBody))]
[JsonSerializable(typeof(PersonEndpoints.CreateDependentBody))]
[JsonSerializable(typeof(PersonEndpoints.PersonDto))]
[JsonSerializable(typeof(PersonEndpoints.PreferencesBody))]
[JsonSerializable(typeof(ProposalEndpoints.ApproveProposalBody))]
[JsonSerializable(typeof(ProposalEndpoints.RejectProposalBody))]
[JsonSerializable(typeof(ProposalEndpoints.SubmitProposalBody))]
[JsonSerializable(typeof(RevokeSessionsBody))]
[JsonSerializable(typeof(ScenarioEndpoints.ScenarioBody))]
[JsonSerializable(typeof(ScenarioEndpoints.SimulateBody))]
[JsonSerializable(typeof(TaxEndpoints.TaxProfileBody))]
[JsonSerializable(typeof(TaxEndpoints.AddTaxAssessmentBody))]
[JsonSerializable(typeof(TaxEndpoints.CorrectTaxAssessmentBody))]
[JsonSerializable(typeof(TaxAssessmentView))]
[JsonSerializable(typeof(TaxAssessmentList))]
[JsonSerializable(typeof(SessionResponse))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;
