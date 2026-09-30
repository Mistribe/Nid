using Nid.Core.Contracts;
using Nid.Investments.Application;
using Nid.Investments.Contracts;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Host.Api.SubscriptionEndpoints;

namespace Nid.Host.Api;

public static class InvestmentEndpoints
{
    public static IEndpointRouteBuilder MapInvestmentEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/investments").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("/envelopes", async (string? scope, bool? archived, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListEnvelopesQuery(ParseScope(scope), archived ?? false), ct)).ToHttp(list => list))
            .Returns<EnvelopeList>();

        api.MapGet("/envelopes/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetEnvelopeQuery(new ItemId(id)), ct)).ToHttp(detail => detail))
            .Returns<EnvelopeDetail>();

        api.MapPost("/envelopes", async (CreateEnvelopeBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(reader);
                return command is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(command, ct)).ToHttp(id => new IdResponse(id.Value), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPatch("/envelopes/{id:guid}", async (Guid id, EditEnvelopeBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(body.ToCommand(new ItemId(id)), ct)).ToHttp(x => new IdResponse(x.Value)))
            .Returns<IdResponse>();

        api.MapPost("/envelopes/{id:guid}/valuations", async (Guid id, ValuationBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new RecordValuationCommand(new ItemId(id), RequireDate(body.AsOf), RequireMoney(body.Amount), body.Note), ct))
                .ToHttp(v => new IdResponse(v), StatusCodes.Status201Created))
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapDelete("/envelopes/{id:guid}/valuations/{valuationId:guid}", async (Guid id, Guid valuationId, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new VoidValuationCommand(new ItemId(id), valuationId), ct)).ToHttp(v => new IdResponse(v)))
            .Returns<IdResponse>();

        api.MapPost("/envelopes/{id:guid}/flows", async (Guid id, FlowBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new RecordFlowCommand(new ItemId(id), body.ToDraft()), ct))
                .ToHttp(f => new IdResponse(f), StatusCodes.Status201Created))
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPut("/envelopes/{id:guid}/flows/{flowId:guid}", async (Guid id, Guid flowId, FlowBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new CorrectFlowCommand(new ItemId(id), flowId, body.ToDraft()), ct)).ToHttp(f => new IdResponse(f)))
            .Returns<IdResponse>();

        api.MapDelete("/envelopes/{id:guid}/flows/{flowId:guid}", async (Guid id, Guid flowId, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new VoidFlowCommand(new ItemId(id), flowId), ct)).ToHttp(f => new IdResponse(f)))
            .Returns<IdResponse>();

        api.MapPost("/transfers", async (TransferBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new TransferCommand(new ItemId(body.From), new ItemId(body.To), RequireDate(body.OccurredOn), RequireMoney(body.Amount), body.Note), ct))
                .ToHttp(t => new IdResponse(t), StatusCodes.Status201Created))
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPost("/envelopes/{id:guid}/close", async (Guid id, CloseBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new CloseEnvelopeCommand(new ItemId(id), RequireDate(body.ClosedOn), body.FinalWithdrawal?.ToMoney()), ct))
                .ToHttp(x => new IdResponse(x.Value)))
            .Returns<IdResponse>();

        api.MapPost("/envelopes/{id:guid}/reopen", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ReopenEnvelopeCommand(new ItemId(id)), ct)).ToHttp(x => new IdResponse(x.Value)))
            .Returns<IdResponse>();

        api.MapGet("/envelopes/{id:guid}/performance", async (Guid id, string from, string to, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetPeriodPerformanceQuery(new ItemId(id), RequireDate(from), RequireDate(to)), ct)).ToHttp(p => p))
            .Returns<PeriodView>();

        api.MapGet("/envelopes/{id:guid}/series", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetEnvelopeSeriesQuery(new ItemId(id)), ct)).ToHttp(s => s))
            .Returns<EnvelopeSeries>();

        return routes;
    }

    /// <summary>
    /// Controller ruling (Task 4, carried): System.Text.Json binds a missing or null body member to a real
    /// null even for a non-nullable type, so a body's <see cref="MoneyBody"/> field must be read through this
    /// rather than <c>.ToMoney()</c> directly — a null instance answers 400, never a <see cref="NullReferenceException"/>.
    /// </summary>
    private static Money RequireMoney(MoneyBody? body)
        => body is null
            ? throw new BadHttpRequestException("An amount is required.")
            : body.ToMoney();

    public sealed record OpeningBody(string OccurredOn, MoneyBody Amount);

    public sealed record FirstValuationBody(string AsOf, MoneyBody Amount);

    public sealed record CreateEnvelopeBody(
        string DisplayName, string Kind, string Currency, string? Visibility, IReadOnlyList<OwnerBody> Owners,
        string? Institution, string? AccountHint, string? OpenedOn, string? ValuationCadence, int? StaleAfterDays, string? Note,
        OpeningBody? Opening, FirstValuationBody? FirstValuation)
    {
        internal CreateEnvelopeCommand? ToCommand(FieldReader reader)
        {
            // FieldReader.Visibility fails a null value rather than defaulting it, unlike Subscriptions' body
            // (whose Visibility is effectively required too): an omitted visibility here defaults to shared.
            var visibility = reader.Visibility(Visibility ?? "shared", "visibility");
            if (!Nid.Core.Contracts.Currency.TryCreate(Currency, out var currency))
                reader.Add("currency", "invalid_currency", "A currency is a three-letter ISO 4217 code, in capitals.");
            var openedOn = reader.Date(OpenedOn, "openedOn", required: false);
            var openingOn = Opening is null ? null : reader.Date(Opening.OccurredOn, "opening.occurredOn");
            var openingAmount = Opening is null ? null : reader.Money(Opening.Amount, "opening.amount");
            var firstOn = FirstValuation is null ? null : reader.Date(FirstValuation.AsOf, "firstValuation.asOf");
            var firstAmount = FirstValuation is null ? null : reader.Money(FirstValuation.Amount, "firstValuation.amount");
            return reader.Ok
                ? new CreateEnvelopeCommand(DisplayName, Kind, currency, visibility!.Value,
                    (Owners ?? []).Select(o => new OwnerShare(new PersonId(o.PersonId), o.ShareBp)).ToList(),
                    Institution, AccountHint, openedOn, ValuationCadence, StaleAfterDays, Note,
                    Opening is null ? null : new OpeningDraft(openingOn!.Value, openingAmount!.Value),
                    FirstValuation is null ? null : new FirstValuationDraft(firstOn!.Value, firstAmount!.Value))
                : null;
        }
    }

    public sealed record EditEnvelopeBody(
        string? DisplayName, string? Kind, string? Institution, bool ClearInstitution, string? AccountHint, bool ClearAccountHint,
        string? OpenedOn, bool ClearOpenedOn, string? ValuationCadence, int? StaleAfterDays, bool ClearStaleAfterDays,
        string? Note, bool ClearNote)
    {
        public EditEnvelopeCommand ToCommand(ItemId id) => new(id, DisplayName, Kind, new EnvelopeChange(
            Institution: Institution, ClearInstitution: ClearInstitution, AccountHint: AccountHint, ClearAccountHint: ClearAccountHint,
            OpenedOn: OptionalDate(OpenedOn), ClearOpenedOn: ClearOpenedOn, StaleAfterDays: StaleAfterDays,
            ClearStaleAfterDays: ClearStaleAfterDays, Note: Note, ClearNote: ClearNote), ValuationCadence);
    }

    // MoneyBody? (not the non-nullable MoneyBody the wire shape implies) because a missing "amount" member
    // binds to null regardless of the declared type (Task 4 controller ruling) — RequireMoney is what turns
    // that null into a located 400 instead of a NullReferenceException.
    public sealed record ValuationBody(string AsOf, MoneyBody? Amount, string? Note);

    public sealed record FlowBody(string OccurredOn, string Kind, MoneyBody? Amount, string? Category, string? ExternalRef, string? Note)
    {
        public FlowDraft ToDraft() => new(RequireDate(OccurredOn), Kind, RequireMoney(Amount), Category, ExternalRef, Note);
    }

    // MoneyBody? (not the non-nullable MoneyBody the wire shape implies) because a missing "amount" member
    // binds to null regardless of the declared type (Task 4 controller ruling) — RequireMoney turns that null
    // into a located 400 instead of a NullReferenceException.
    public sealed record TransferBody(Guid From, Guid To, string OccurredOn, MoneyBody? Amount, string? Note);

    public sealed record CloseBody(string ClosedOn, MoneyBody? FinalWithdrawal);
}
