using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nid.Core.Application.Projection;
using Nid.Host.Api;

namespace Nid.Host.Mcp;

[McpServerToolType]
public sealed class ProjectionTools
{
    [McpServerTool(Name = "get_projection", ReadOnly = true)]
    [Description("The household's projected net worth to a horizon, at today, each December 31st and the horizon's last month, in nominal and today's money, with milestones (loan ends) and the assumptions used. Indicative: assumed returns, no future tax. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetProjection(IServiceProvider services,
        [Description("'household' (default) or 'me'.")] string? scope = null,
        [Description("'10y' (default), '5y', '20y', '30y', 'Ny' (1 to 40) or 'retire:<personId>'.")] string? horizon = null,
        CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetProjectionQuery(SubscriptionEndpoints.ParseScope(scope), ProjectionEndpoints.ParseHorizon(horizon)),
            YearEnds, ct);

    /// <summary>Spec §7: the assistant reads the year ends (31 December), with today's point and the last; the rest is the route's answer.</summary>
    internal static ProjectionView YearEnds(ProjectionView view)
        => view with
        {
            Points = view.Points.Where((p, i) => i == 0 || i == view.Points.Count - 1 || p.On is { Month: 12, Day: 31 }).ToList(),
        };
}
