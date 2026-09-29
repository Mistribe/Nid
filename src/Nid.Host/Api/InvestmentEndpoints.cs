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

        return routes;
    }

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
}
