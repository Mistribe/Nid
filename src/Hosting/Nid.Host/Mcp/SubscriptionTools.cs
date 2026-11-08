using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nid.Core.Contracts;
using Nid.Host.Api;
using Nid.Subscriptions.Application;
using Nid.Subscriptions.Application.Catalog;

namespace Nid.Host.Mcp;

/// <summary>A provider a subscription can name; the route's labels and icons would push a whole catalog past the result limit.</summary>
public sealed record ProviderKeyView(string Key, string Name, string Origin);

[McpServerToolType]
public sealed class SubscriptionTools
{
    private const string Window = "ISO date; optional, as on the REST route.";

    [McpServerTool(Name = "list_subscriptions", ReadOnly = true)]
    [Description("The subscriptions with their real monthly cost and next charge, and spent: what each has cost since its first "
        + "charge (since, total, last12Months, in the reference currency; partial when a rate was missing). " + McpSetup.Untrusted)]
    public static Task<CallToolResult> ListSubscriptions(IServiceProvider services,
        [Description("'household' (default) or 'me'.")] string? scope = null, [Description(Window)] string? from = null,
        [Description(Window)] string? to = null, [Description(Window)] string? horizon = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new ListSubscriptionsQuery(SubscriptionEndpoints.ParseScope(scope),
            SubscriptionEndpoints.OptionalDate(from), SubscriptionEndpoints.OptionalDate(to), SubscriptionEndpoints.OptionalDate(horizon)), ct);

    /// <summary>Issue #284: the providers of <c>GET /api/v1/subscriptions/providers</c> that a subscription can name, so without the archived ones.</summary>
    [McpServerTool(Name = "list_providers", ReadOnly = true)]
    [Description("The provider keys a subscription can name (providerKey of propose_edit_subscription): the instance's catalog and your custom " +
                 "providers, with name and origin ('catalog' or 'custom'). A provider missing here is not in this instance's catalog. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> ListProviders(IServiceProvider services,
        [Description("Part of the provider's name.")] string? q = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new ListProvidersQuery(CatalogEndpoints.RequireSearch(q)),
            list => list.Where(p => !p.Archived).Select(p => new ProviderKeyView(p.Key, p.Name, p.Origin)).ToList(), ct);

    [McpServerTool(Name = "get_subscription", ReadOnly = true)]
    [Description("One subscription: its phases, purchases, costs and next charge. It also returns contractTerms (anniversaryOn, "
        + "notice {count, unit: day, week, month or year}, commitmentEndsOn, cancellableFrom: ISO dates, what the household typed) and "
        + "cancellation (the computed last day to cancel, or from when it is free). It also returns insurance (kind, insured label, "
        + "policy number, guarantees, the linked property): its property is null when there is no link or when the person cannot "
        + "read the linked property, so a null property can hide a link. spent is what it has cost since its first charge (since, "
        + "total, last12Months; partial when a rate was missing); saved, for an ended subscription no other subscription of its "
        + "provider replaced, is what it would have cost since its end at its last price (lastPrice every every). " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetSubscription(IServiceProvider services, [Description("The subscription's id.")] Guid id,
        [Description(Window)] string? from = null, [Description(Window)] string? to = null, [Description(Window)] string? horizon = null,
        CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetSubscriptionQuery(new ItemId(id),
            SubscriptionEndpoints.OptionalDate(from), SubscriptionEndpoints.OptionalDate(to), SubscriptionEndpoints.OptionalDate(horizon)), ct);
}
