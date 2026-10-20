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

    [McpServerTool(Name = "get_envelope_holdings", ReadOnly = true)]
    [Description("An envelope's holdings (funds, euro fund…) at its latest holdings snapshot on or before a date: each line, weight, gain "
        + "and annual return, the unallocated remainder and the warnings. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetEnvelopeHoldings(IServiceProvider services, [Description("The envelope's id.")] Guid id,
        [Description("ISO date, default today.")] string? on = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetHoldingsQuery(new ItemId(id), SubscriptionEndpoints.OptionalDate(on)), ct);

    [McpServerTool(Name = "list_holding_snapshots", ReadOnly = true)]
    [Description("An envelope's recorded compositions (snapshots), with their ids and lines. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> ListHoldingSnapshots(IServiceProvider services, [Description("The envelope's id.")] Guid id,
        CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new ListHoldingsSnapshotsQuery(new ItemId(id)), ct);

    [McpServerTool(Name = "list_holding_movements", ReadOnly = true)]
    [Description("The movements of one holding of an envelope (allocations, switches, fees, interest). " + McpSetup.Untrusted)]
    public static Task<CallToolResult> ListHoldingMovements(IServiceProvider services, [Description("The envelope's id.")] Guid id,
        [Description("The holding's id (get_envelope_holdings).")] Guid holdingId, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new ListHoldingMovementsQuery(new ItemId(id), holdingId), ct);

    [McpServerTool(Name = "get_holding_performance", ReadOnly = true)]
    [Description("One holding's performance between two dates: start and end values, money in − out, gain. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetHoldingPerformance(IServiceProvider services, [Description("The envelope's id.")] Guid id,
        [Description("The holding's id (get_envelope_holdings).")] Guid holdingId,
        [Description("ISO start date.")] string from, [Description("ISO end date.")] string to, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetHoldingPerformanceQuery(new ItemId(id), holdingId,
            SubscriptionEndpoints.RequireDate(from), SubscriptionEndpoints.RequireDate(to)), ct);
}
