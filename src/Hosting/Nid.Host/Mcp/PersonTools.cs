using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Host.Api;

namespace Nid.Host.Mcp;

[McpServerToolType]
public sealed class PersonTools
{
    [McpServerTool(Name = "list_persons", ReadOnly = true)]
    [Description("Lists the household's persons (adults and dependents) with their ids, to know whom an item or an entry belongs to. " + McpSetup.Untrusted)]
    public static async Task<CallToolResult> ListPersons(IServiceProvider services, CancellationToken ct)
    {
        var today = await services.GetRequiredService<IHouseholdCalendar>().TodayAsync(ct);
        return await McpCalls.RunAsync(services, () => new ListPersonsQuery(), list => list.Select(p => PersonEndpoints.ToDto(p, today)).ToList(), ct);
    }
}
