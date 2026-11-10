using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nid.Core.Application.Dashboard;
using Nid.Core.Application.Reports;
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

    [McpServerTool(Name = "get_month_changes", ReadOnly = true)]
    [Description("What changed since a month ago: net worth, commitments and income, each with its change and its three largest causes. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetMonthChanges(IServiceProvider services, [Description(Scope)] string? scope = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetMonthChangesQuery(SubscriptionEndpoints.ParseScope(scope)), ct);

    /// <summary>Issue #12: the same view as <c>GET /api/v1/reports/year</c>.</summary>
    [McpServerTool(Name = "get_year_review", ReadOnly = true)]
    [Description("The annual review of a year, from 31 December before it to its 31 December (today for this year): the net worth's " +
                 "change in parts, the investments, the income and engagements, the subscriptions, loans, properties, goals and tax estimate, " +
                 "most beside the year before; a section is null when it has nothing to show, years lists the years on offer. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetYearReview(IServiceProvider services,
        [Description("The year to review (default: this year).")] int? year = null,
        [Description(Scope)] string? scope = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetYearReviewQuery(year, SubscriptionEndpoints.ParseScope(scope)), ct);

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
