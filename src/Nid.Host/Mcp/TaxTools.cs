using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nid.Core.Application.Tax;
using Nid.Host.Api;

namespace Nid.Host.Mcp;

[McpServerToolType]
public sealed class TaxTools
{
    /// <summary>Rental 2044 spec §7: the same view as <c>GET /api/v1/tax/2044</c>.</summary>
    [McpServerTool(Name = "get_rental_2044", ReadOnly = true)]
    [Description("The 2044 rental income form of a revenue year: each rented property's lines (211 to 263, whole euros), the household's " +
                 "result and deficit split, the 2042 boxes to copy (4BA, 4BB, 4BC; 4BE under the micro-foncier) and the expenses to check. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetRental2044(IServiceProvider services,
        [Description("The revenue year (default: last year).")] int? year = null,
        [Description("'household' (default) or 'me'.")] string? scope = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetRental2044Query(year, SubscriptionEndpoints.ParseScope(scope)), ct);
}
