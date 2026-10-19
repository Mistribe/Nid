using System.Text.Json;
using Nid.Core.Application;
using Nid.Core.Contracts;
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
                    : Results.Json(submitted.Proposal, statusCode: submitted.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
            })
            .Returns<ProposalView>(StatusCodes.Status201Created)
            .Produces<ProposalView>(StatusCodes.Status200OK) // a deduplicated submission
            .Produces(StatusCodes.Status204NoContent); // a conforme document

        api.MapGet("", async (string? status, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new ListProposalsQuery(status), ct)).ToHttp(list => list))
            .Returns<ProposalList>();

        api.MapGet("/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetProposalQuery(id), ct)).ToHttp(detail => detail))
            .Returns<ProposalDetail>();

        api.MapPost("/{id:guid}/approve", async (Guid id, ApproveProposalBody body, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new ApproveProposalCommand(id, body.Version, body.EditedPayload), ct)).ToHttp(view => view))
            .Returns<ProposalView>();

        api.MapPost("/{id:guid}/reject", async (Guid id, RejectProposalBody body, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new RejectProposalCommand(id, body.Version, body.Note), ct)).ToHttp(view => view))
            .Returns<ProposalView>();

        return routes;
    }
}
