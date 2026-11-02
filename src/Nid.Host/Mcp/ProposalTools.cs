using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nid.Core.Application;
using Nid.Host.Api;
using Nid.Platform.Contracts;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Mcp;

[McpServerToolType]
public sealed class ProposalTools
{
    /// <summary>Issue #291: the same view as <c>GET /api/v1/core/proposals</c>.</summary>
    [McpServerTool(Name = "list_proposals", ReadOnly = true)]
    [Description("The proposals waiting in « À valider », or those of another status, with their payloads and decisions: check them before proposing again, " +
                 "and read decisionNote to learn why one was rejected. Deciding happens in the browser only. Payloads and source labels may be copied from documents. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> ListProposals(IServiceProvider services,
        [Description("'pending' (default), 'approved', 'rejected' or 'superseded'.")] string? status = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new ListProposalsQuery(status),
            async list => list with { Items = await ProposalEndpoints.HideDocumentsAsync(services.GetRequiredService<IInvoker>(), list.Items, ct) }, ct);

    /// <summary>Issue #291: the same view as <c>GET /api/v1/core/proposals/{id}</c>.</summary>
    [McpServerTool(Name = "get_proposal", ReadOnly = true)]
    [Description("One proposal: its payload, the edited payload if any, the preview of a pending one, and the decision with its note. " +
                 "canDecide is always false here: deciding happens in the browser only. Payloads and source labels may be copied from documents. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetProposal(IServiceProvider services,
        [Description("The proposal's id.")] Guid id, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetProposalQuery(id),
            async detail => detail with { Proposal = (await ProposalEndpoints.HideDocumentsAsync(services.GetRequiredService<IInvoker>(), [detail.Proposal], ct))[0] }, ct);

    /// <summary>Not a decision: a token takes back its person's own pending proposal, nothing else (<see cref="DeleteProposalCommand"/>).</summary>
    [McpServerTool(Name = "delete_proposal", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Deletes a pending proposal submitted through your own token, for instance a duplicate; it cannot be restored. A proposal already decided, " +
                 "submitted by someone else or from the browser is refused. Needs the 'write' scope. Ids and versions come from list_proposals.")]
    public static async Task<CallToolResult> DeleteProposal(IServiceProvider services,
        [Description("The proposal's id.")] Guid id,
        [Description("The proposal's version.")] uint version, CancellationToken ct = default)
    {
        if (services.GetRequiredService<IActorAccessor>().Current?.Has(ScopeNames.Write) != true)
            return McpCalls.Error($"Refused: deleting a proposal needs the '{ScopeNames.Write}' scope.");
        var deleted = await services.GetRequiredService<IInvoker>().InvokeAsync(new DeleteProposalCommand(id, version), ct);
        return deleted.TryGetFailure(out var failure)
            ? McpCalls.Refusal(failure!)
            : new CallToolResult { Content = [new TextContentBlock { Text = "Deleted." }] };
    }
}
