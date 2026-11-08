using System.Text.Json;
using System.Text.Json.Serialization;
using Nid.Subscriptions.Application;
using Nid.Subscriptions.Application.Catalog;

namespace Nid.Host.Api.Json;

/// <summary>The Subscriptions module's request and response bodies, for NidJsonContexts.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.Strict,
    Converters = [typeof(ItemIdJsonConverter), typeof(LocalDateJsonConverter)])]
[JsonSerializable(typeof(IReadOnlyList<LabelView>))]
[JsonSerializable(typeof(IReadOnlyList<ProviderView>))]
[JsonSerializable(typeof(List<Nid.Host.Mcp.ProviderKeyView>))]
[JsonSerializable(typeof(CatalogEndpoints.CreateProviderBody))]
[JsonSerializable(typeof(SubscriptionEndpoints.ClosePhaseBody))]
[JsonSerializable(typeof(SubscriptionEndpoints.CreateSubscriptionBody))]
[JsonSerializable(typeof(SubscriptionEndpoints.EditSubscriptionBody))]
[JsonSerializable(typeof(SubscriptionEndpoints.IdResponse))]
[JsonSerializable(typeof(SubscriptionEndpoints.InsuranceBody))]
[JsonSerializable(typeof(SubscriptionEndpoints.PhaseBody))]
[JsonSerializable(typeof(SubscriptionEndpoints.PriceChangeBody))]
[JsonSerializable(typeof(SubscriptionEndpoints.PurchaseInput))]
[JsonSerializable(typeof(CatalogRunsView))]
[JsonSerializable(typeof(CatalogSyncRequested))]
[JsonSerializable(typeof(ProviderView))]
[JsonSerializable(typeof(SubscriptionCharges))]
[JsonSerializable(typeof(SubscriptionDetail))]
[JsonSerializable(typeof(SubscriptionList))]
[JsonSerializable(typeof(SubscriptionsOverview))]
internal sealed partial class SubscriptionsJsonContext : JsonSerializerContext;
