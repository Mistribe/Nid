using Nid.Core.Application.Projection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Host.Api.SubscriptionEndpoints;

namespace Nid.Host.Api;

public static class ProjectionScenarioEndpoints
{
    /// <summary>Net worth projection spec §7: the projection scenarios' own routes, beside the purchases' <c>/api/v1/scenarios</c>.</summary>
    public static IEndpointRouteBuilder MapProjectionScenarioEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/scenarios/projection").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        // Spec §7: the base and the document's scenario; nothing is written.
        api.MapPost("/simulate", async (SimulateProjectionBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var document = ProjectionDocumentView.Read(body.Document);
                return document.TryGetValue(out var read)
                    ? (await invoker.InvokeAsync(new SimulateProjectionScenarioQuery(ParseScope(body.Scope), read), ct)).ToHttp(view => view)
                    : document.ToHttp(_ => new { });
            })
            .Returns<ProjectionComparisonView>();

        // Spec §6 and §7: a saved projection scenario; a purchase scenario's id answers 404 here, as a projection's does on the
        // purchase routes. The list and the deletion are the purchases' routes.
        api.MapPost("", async (ProjectionScenarioBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var (name, visibility, document, problem) = body.Read();
                if (problem is not null) return problem;
                var owners = (body.Owners ?? []).Select(o => new OwnerShare(new PersonId(o.PersonId), o.ShareBp)).ToList();
                return (await invoker.InvokeAsync(new CreateProjectionScenarioCommand(name!, visibility, owners, ParseScope(body.Scope), document!), ct))
                    .ToHttp(id => new IdResponse(id.Value), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapGet("/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetProjectionScenarioQuery(new ItemId(id)), ct)).ToHttp(view => view))
            .Returns<ProjectionScenarioView>();

        api.MapPut("/{id:guid}", async (Guid id, ProjectionScenarioBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var (name, visibility, document, problem) = body.Read();
                if (problem is not null) return problem;
                return (await invoker.InvokeAsync(new ReplaceProjectionScenarioCommand(new ItemId(id), name!, visibility, ParseScope(body.Scope), document!), ct))
                    .ToHttp(view => view);
            })
            .Returns<ProjectionScenarioView>();

        return routes;
    }

    public sealed record SimulateProjectionBody(string? Scope, ProjectionDocumentView? Document);

    /// <summary>The document is the shape the reads answer with (<see cref="ProjectionDocumentView"/>); owners are read on creation only.</summary>
    public sealed record ProjectionScenarioBody(
        string? Name, string? Visibility, IReadOnlyList<OwnerBody>? Owners, string? Scope, ProjectionDocumentView? Document)
    {
        /// <summary>
        /// The name, the visibility and the document, or the problem: a name or visibility refused lists every field (the document's
        /// too) as the request's shape; a document alone refused answers as the simulation does (<c>projection.invalid_field</c>).
        /// </summary>
        internal (string? Name, Visibility Visibility, ProjectionScenario? Document, Microsoft.AspNetCore.Http.IResult? Problem) Read()
        {
            var reader = new FieldReader();
            var name = reader.Text(Name, "name");
            var visibility = reader.Visibility(Visibility ?? "shared", "visibility");
            var document = ProjectionDocumentView.Read(Document);
            if (!reader.Ok)
            {
                if (document.TryGetFailure(out var failure) && FieldErrors.Of(failure!) is { } errors)
                    foreach (var error in errors) reader.Add(error.Path, error.Code, error.Message);
                return (null, default, null, reader.Problem());
            }
            return document.TryGetValue(out var read)
                ? (name, visibility!.Value, read, null)
                : (null, default, null, document.ToHttp(_ => new { }));
        }
    }
}
