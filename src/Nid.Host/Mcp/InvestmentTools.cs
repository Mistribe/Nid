using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nid.Core.Contracts;
using Nid.Host.Api;
using Nid.Investments.Application;

namespace Nid.Host.Mcp;

[McpServerToolType]
public sealed class InvestmentTools
{
    [McpServerTool(Name = "list_envelopes", ReadOnly = true)]
    [Description("The investment envelopes (PEA, life insurance…) with their latest value and gain. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> ListEnvelopes(IServiceProvider services,
        [Description("'household' (default) or 'me'.")] string? scope = null, [Description("Include closed envelopes.")] bool archived = false,
        CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new ListEnvelopesQuery(SubscriptionEndpoints.ParseScope(scope), archived), ct);

    [McpServerTool(Name = "get_envelope", ReadOnly = true)]
    [Description("One envelope: its values, contributions and gain. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetEnvelope(IServiceProvider services, [Description("The envelope's id.")] Guid id, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetEnvelopeQuery(new ItemId(id)), ct);

    [McpServerTool(Name = "get_envelope_performance", ReadOnly = true)]
    [Description("An envelope's performance between two dates: start and end values, net contributions, gain. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetEnvelopePerformance(IServiceProvider services, [Description("The envelope's id.")] Guid id,
        [Description("ISO start date.")] string from, [Description("ISO end date.")] string to, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetPeriodPerformanceQuery(new ItemId(id),
            SubscriptionEndpoints.RequireDate(from), SubscriptionEndpoints.RequireDate(to)), ct);
}
