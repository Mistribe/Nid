using System.Text.Json;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Documents.Application;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Api;

/// <summary>Spec 4C1 §3–§5: the validation queue.</summary>
public static class ProposalEndpoints
{
    public sealed record SubmitProposalBody(string Kind, Guid? TargetItemId, JsonElement Payload, string? SourceLabel);

    public sealed record ApproveProposalBody(uint Version, JsonElement? EditedPayload);

    public sealed record RejectProposalBody(uint Version, string? Note);

    public static IEndpointRouteBuilder MapProposalEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/core/proposals").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapPost("", async (SubmitProposalBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var result = await invoker.InvokeAsync(new SubmitProposalCommand(body.Kind, body.TargetItemId is { } target ? new ItemId(target) : null, body.Payload, body.SourceLabel), ct);
                if (!result.TryGetValue(out var submitted))
                    return result.ToHttp(s => s.Proposal);
                return submitted!.Proposal is null
                    ? Results.NoContent() // spec 4D1 D3: the document matches what Nid expects, nothing is created
                    : ResultHttp.Json((await HideDocumentsAsync(invoker, [submitted.Proposal], ct))[0],
                        submitted.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
            })
            .Returns<ProposalView>(StatusCodes.Status201Created)
            .Produces<ProposalView>(StatusCodes.Status200OK) // a deduplicated submission
            .Produces(StatusCodes.Status204NoContent); // a conforme document

        api.MapGet("", async (string? status, IInvoker invoker, CancellationToken ct)
                => await ShownAsync(await invoker.InvokeAsync(new ListProposalsQuery(status), ct),
                    async list => list with { Items = await HideDocumentsAsync(invoker, list.Items, ct) }))
            .Returns<ProposalList>();

        api.MapGet("/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
                => await ShownAsync(await invoker.InvokeAsync(new GetProposalQuery(id), ct),
                    async detail => detail with { Proposal = (await HideDocumentsAsync(invoker, [detail.Proposal], ct))[0] }))
            .Returns<ProposalDetail>();

        api.MapPost("/{id:guid}/approve", async (Guid id, ApproveProposalBody body, IInvoker invoker, CancellationToken ct)
                => await ShownAsync(await invoker.InvokeAsync(new ApproveProposalCommand(id, body.Version, body.EditedPayload), ct),
                    async view => (await HideDocumentsAsync(invoker, [view], ct))[0]))
            .Returns<ProposalView>();

        api.MapPost("/{id:guid}/reject", async (Guid id, RejectProposalBody body, IInvoker invoker, CancellationToken ct)
                => await ShownAsync(await invoker.InvokeAsync(new RejectProposalCommand(id, body.Version, body.Note), ct),
                    async view => (await HideDocumentsAsync(invoker, [view], ct))[0]))
            .Returns<ProposalView>();

        return routes;
    }

    /// <summary>
    /// Tranche 3 design §4.1: a proposal view leaves the host (REST and MCP) with its <c>documentId</c> only for a reader of that document;
    /// one batched check per response. A refused check hides them all.
    /// </summary>
    internal static async Task<IReadOnlyList<ProposalView>> HideDocumentsAsync(IInvoker invoker, IReadOnlyList<ProposalView> views, CancellationToken ct)
    {
        var cited = views.Select(v => v.DocumentId).OfType<Guid>().ToHashSet();
        if (cited.Count == 0)
            return views;
        var readable = (await invoker.InvokeAsync(new FilterReadableDocumentsQuery(cited), ct)).TryGetValue(out var ids) ? ids! : new HashSet<Guid>();
        return views.Select(v => v.DocumentId is { } d && !readable.Contains(d) ? v with { DocumentId = null } : v).ToList();
    }

    private static async Task<Microsoft.AspNetCore.Http.IResult> ShownAsync<T>(Result<T> result, Func<T, Task<T>> hide)
        where T : notnull
        => result.TryGetValue(out var value) ? ResultHttp.Json(await hide(value!)) : result.ToHttp(v => v);
}
