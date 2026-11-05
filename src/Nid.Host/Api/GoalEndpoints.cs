using Nid.Core.Contracts;
using Nid.Goals.Application;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Host.Api.RealEstateEndpoints;
using static Nid.Host.Api.SubscriptionEndpoints;

namespace Nid.Host.Api;

/// <summary>Savings goals spec §5: the goal reads and writes. Amounts are decimal strings in the household's reference currency.</summary>
public static class GoalEndpoints
{
    public static IEndpointRouteBuilder MapGoalEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/goals").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("", async (string? scope, bool? closed, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListGoalsQuery(ParseScope(scope), closed ?? false), ct)).ToHttp(list => list))
            .Returns<GoalList>();

        // Issue #412: the creation form shows an emergency fund's target before it is saved.
        api.MapGet("/emergency-target", async (int? months, string? scope, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new EmergencyTargetQuery(months ?? 6, ParseScope(scope)), ct)).ToHttp(view => view))
            .Returns<EmergencyTargetView>();

        api.MapGet("/{id:guid}", async (Guid id, string? scope, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetGoalQuery(new ItemId(id), ParseScope(scope)), ct)).ToHttp(detail => detail))
            .Returns<GoalDetail>();

        api.MapPost("", async (CreateGoalBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(reader);
                return command is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(command, ct)).ToHttp(id => new IdResponse(id.Value), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPut("/{id:guid}", async (Guid id, EditGoalBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(new ItemId(id), reader);
                return command is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(command, ct)).ToHttp(x => new IdResponse(x.Value));
            })
            .Returns<IdResponse>();

        api.MapPost("/{id:guid}/close", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new CloseGoalCommand(new ItemId(id)), ct)).ToHttp(x => new IdResponse(x.Value)))
            .Returns<IdResponse>();

        api.MapPost("/{id:guid}/reopen", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ReopenGoalCommand(new ItemId(id)), ct)).ToHttp(x => new IdResponse(x.Value)))
            .Returns<IdResponse>();

        api.MapPut("/{id:guid}/progress/{on}", async (Guid id, string on, GoalProgressBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var date = reader.Date(on, "on");
                var amount = LoanBodies.Number(reader, body.Amount, "amount");
                return !reader.Ok
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new PutGoalProgressCommand(new ItemId(id), date!.Value, amount!.Value), ct)).ToHttp(x => new IdResponse(x.Value));
            })
            .Returns<IdResponse>();

        api.MapDelete("/{id:guid}/progress/{on}", async (Guid id, string on, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                if (reader.Date(on, "on") is not { } date)
                    return reader.Problem();
                // A 204 carries no body: ToHttp would write one.
                var result = await invoker.InvokeAsync(new DeleteGoalProgressCommand(new ItemId(id), date), ct);
                return result.TryGetValue(out _) ? Results.NoContent() : result.ToHttp(v => v);
            })
            .ReturnsNoContent();

        return routes;
    }

    /// <summary>An envelope and its share in basis points, all of it (10000) when not given (D2).</summary>
    public sealed record GoalFundingBody(Guid? EnvelopeId, int? ShareBp);

    /// <summary>
    /// <c>targetAmount</c> for a savings goal, <c>months</c> for an emergency fund (6 when not given). Every member is read through
    /// <see cref="FieldReader"/> or checked by the handler: a missing one binds to null and answers 422 at its path.
    /// </summary>
    public sealed record CreateGoalBody(
        string? Name, string? Kind, string? TargetAmount, int? Months, string? TargetDate, string? Note,
        IReadOnlyList<GoalFundingBody>? Funding, string? Visibility, IReadOnlyList<OwnerBody>? Owners, string? ShareLevel = null)
    {
        internal CreateGoalCommand? ToCommand(FieldReader reader)
        {
            var visibility = reader.Visibility(Visibility, "visibility");
            var level = reader.ShareLevel(ShareLevel, "shareLevel");
            var target = LoanBodies.Number(reader, TargetAmount, "targetAmount", required: false);
            var targetDate = reader.Date(TargetDate, "targetDate", required: false);
            var funding = GoalBodies.Funding(reader, Funding);
            return reader.Ok
                ? new CreateGoalCommand(Name!, Kind ?? "", target, Months, targetDate, Note, funding, visibility!.Value,
                    (Owners ?? []).Select(o => new OwnerShare(new PersonId(o.PersonId), o.ShareBp)).ToList(), level!.Value)
                : null;
        }
    }

    /// <summary>
    /// Replaces every editable field (the kind, the visibility and the owners stay), with the <c>version</c> read with the goal. A
    /// missing version answers 422 at <c>version</c>: it would otherwise bind to 0 and pass for a conflict.
    /// </summary>
    public sealed record EditGoalBody(
        string? Name, string? TargetAmount, int? Months, string? TargetDate, string? Note, IReadOnlyList<GoalFundingBody>? Funding, uint? Version)
    {
        internal EditGoalCommand? ToCommand(ItemId id, FieldReader reader)
        {
            var target = LoanBodies.Number(reader, TargetAmount, "targetAmount", required: false);
            var targetDate = reader.Date(TargetDate, "targetDate", required: false);
            var funding = GoalBodies.Funding(reader, Funding);
            if (Version is null)
                reader.Add("version", "request.required", "The version read with the goal is required.");
            return reader.Ok ? new EditGoalCommand(id, Name!, target, Months, targetDate, Note, funding, Version!.Value) : null;
        }
    }

    public sealed record GoalProgressBody(string? Amount);

    private static class GoalBodies
    {
        public static IReadOnlyList<FundingShare> Funding(FieldReader reader, IReadOnlyList<GoalFundingBody>? funding)
        {
            var shares = new List<FundingShare>();
            foreach (var (link, i) in (funding ?? []).Select((f, i) => (f, i)))
            {
                // Nullable annotations stop at collection elements: a null in the array binds as such.
                if (link?.EnvelopeId is not { } envelope)
                    reader.Add($"funding[{i}].envelopeId", "request.required", "An envelope is required.");
                else
                    shares.Add(new FundingShare(new ItemId(envelope), link.ShareBp ?? 10_000));
            }
            return shares;
        }
    }
}
