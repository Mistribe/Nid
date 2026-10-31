using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nid.Core.Contracts;
using Nid.Host.Api;
using Nid.Subscriptions.Application;

namespace Nid.Host.Mcp;

[McpServerToolType]
public sealed class SubscriptionTools
{
    private const string Window = "ISO date; optional, as on the REST route.";

    [McpServerTool(Name = "list_subscriptions", ReadOnly = true)]
    [Description("The subscriptions with their real monthly cost and next charge. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> ListSubscriptions(IServiceProvider services,
        [Description("'household' (default) or 'me'.")] string? scope = null, [Description(Window)] string? from = null,
        [Description(Window)] string? to = null, [Description(Window)] string? horizon = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new ListSubscriptionsQuery(SubscriptionEndpoints.ParseScope(scope),
            SubscriptionEndpoints.OptionalDate(from), SubscriptionEndpoints.OptionalDate(to), SubscriptionEndpoints.OptionalDate(horizon)), ct);

    [McpServerTool(Name = "get_subscription", ReadOnly = true)]
    [Description("One subscription: its phases, purchases, costs and next charge. It also returns contractTerms (anniversaryOn, "
        + "notice {count, unit: day, week, month or year}, commitmentEndsOn, cancellableFrom: ISO dates, what the household typed) and "
        + "cancellation (the computed last day to cancel, or from when it is free). It also returns insurance (kind, insured label, "
        + "policy number, guarantees, the linked property): its property is null when there is no link or when the person cannot "
        + "read the linked property, so a null property can hide a link. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetSubscription(IServiceProvider services, [Description("The subscription's id.")] Guid id,
        [Description(Window)] string? from = null, [Description(Window)] string? to = null, [Description(Window)] string? horizon = null,
        CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetSubscriptionQuery(new ItemId(id),
            SubscriptionEndpoints.OptionalDate(from), SubscriptionEndpoints.OptionalDate(to), SubscriptionEndpoints.OptionalDate(horizon)), ct);
}
