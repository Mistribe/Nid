using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nid.Core.Contracts;
using Nid.Host.Api;
using Nid.Income.Application;

namespace Nid.Host.Mcp;

[McpServerToolType]
public sealed class IncomeTools
{
    [McpServerTool(Name = "list_income_sources", ReadOnly = true)]
    [Description("The income sources (salaries, pensions, rents…) with their current monthly amount. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> ListIncomeSources(IServiceProvider services,
        [Description("'household' (default) or 'me'.")] string? scope = null, [Description("Include ended sources.")] bool archived = false,
        CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new ListIncomeSourcesQuery(SubscriptionEndpoints.ParseScope(scope), archived), ct);

    [McpServerTool(Name = "get_income_source", ReadOnly = true)]
    [Description("One income source: its periods and amounts. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetIncomeSource(IServiceProvider services, [Description("The income source's id.")] Guid id, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetIncomeSourceQuery(new ItemId(id)), ct);
}
