using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nid.Core.Contracts;
using Nid.Goals.Application;
using Nid.Host.Api;

namespace Nid.Host.Mcp;

[McpServerToolType]
public sealed class GoalTools
{
    [McpServerTool(Name = "get_goals", ReadOnly = true)]
    [Description("The savings goals and emergency fund with their progress, monthly pace and status, late ones first. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetGoals(IServiceProvider services,
        [Description("'household' (default) or 'me'.")] string? scope = null, [Description("Include closed goals.")] bool closed = false,
        CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new ListGoalsQuery(SubscriptionEndpoints.ParseScope(scope), closed), ct);

    [McpServerTool(Name = "get_goal", ReadOnly = true)]
    [Description("One goal: its progress, the envelopes funding it or its progress entries, and for an emergency fund the commitments behind its target. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetGoal(IServiceProvider services, [Description("The goal's id.")] Guid id,
        [Description("'household' (default) or 'me': the envelopes counted.")] string? scope = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetGoalQuery(new ItemId(id), SubscriptionEndpoints.ParseScope(scope)), ct);
}
