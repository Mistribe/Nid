using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nid.Core.Application.Dashboard;
using Nid.Host.Api;
using Nid.Investments.Application;
using Nid.RealEstate.Application;

namespace Nid.Host.Mcp;

[McpServerToolType]
public sealed class DashboardTools
{
    private const string Scope = "'household' (default) or 'me'.";

    [McpServerTool(Name = "get_dashboard", ReadOnly = true)]
    [Description("The household dashboard: net worth and its split, monthly commitments, income and margin, upcoming events. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetDashboard(IServiceProvider services, [Description(Scope)] string? scope = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetDashboardQuery(SubscriptionEndpoints.ParseScope(scope)), ct);

    [McpServerTool(Name = "get_net_worth_history", ReadOnly = true)]
    [Description("Monthly points of the net worth and contributions over a period. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetNetWorthHistory(IServiceProvider services,
        [Description(Scope)] string? scope = null, [Description("'12m' (default), '3y' or 'all'.")] string? period = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetNetWorthHistoryQuery(SubscriptionEndpoints.ParseScope(scope), DashboardEndpoints.ParsePeriod(period)), ct);

    [McpServerTool(Name = "get_routine", ReadOnly = true)]
    [Description("The monthly routine for investments and income: what is left to record this month (envelope values, income to confirm). " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetRoutine(IServiceProvider services,
        [Description("ISO date of the month to read (default: today).")] string? on = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetRoutineQuery(SubscriptionEndpoints.OptionalDate(on)), ct);

    [McpServerTool(Name = "get_property_routine", ReadOnly = true)]
    [Description("The monthly routine for real estate: estimates due, planned costs and rents to confirm. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetPropertyRoutine(IServiceProvider services,
        [Description("ISO date of the month to read (default: today).")] string? on = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetPropertyRoutineQuery(SubscriptionEndpoints.OptionalDate(on)), ct);
}
