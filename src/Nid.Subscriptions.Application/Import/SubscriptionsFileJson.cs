using System.Text.Json.Serialization;
using Nid.Subscriptions.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Subscriptions.Application.Import;

/// <summary>This module's import-file records, source-generated for NativeAOT, with <see cref="NidDataJson"/>'s rules.</summary>
[JsonSerializable(typeof(SubscriptionRecord))]
[JsonSerializable(typeof(BillingPhaseRecord))]
[JsonSerializable(typeof(PurchaseRecord))]
[JsonSerializable(typeof(ProviderRecord))]
internal sealed partial class SubscriptionsFileJson : JsonSerializerContext
{
    public static SubscriptionsFileJson Records { get; } = new(NidDataJson.NewOptions());
}
