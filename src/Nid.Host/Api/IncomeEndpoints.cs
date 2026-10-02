using Nid.Core.Contracts;
using Nid.Income.Application;
using Nid.Income.Contracts;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Host.Api.SubscriptionEndpoints;

namespace Nid.Host.Api;

public static class IncomeEndpoints
{
    public static IEndpointRouteBuilder MapIncomeEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/income").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("/sources", async (string? scope, bool? archived, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListIncomeSourcesQuery(ParseScope(scope), archived ?? false), ct)).ToHttp(list => list))
            .Returns<IncomeSourceList>();

        api.MapGet("/sources/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetIncomeSourceQuery(new ItemId(id)), ct)).ToHttp(detail => detail))
            .Returns<IncomeSourceDetail>();

        api.MapPost("/sources", async (CreateIncomeSourceBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(reader);
                return command is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(command, ct)).ToHttp(id => new IdResponse(id.Value), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPatch("/sources/{id:guid}", async (Guid id, EditIncomeSourceBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(body.ToCommand(new ItemId(id)), ct)).ToHttp(x => new IdResponse(x.Value)))
            .Returns<IdResponse>();

        return routes;
    }

    /// <summary>
    /// Spec §4: the visibility is required, with no default on the wire (unlike an envelope's). Every member is read
    /// through <see cref="FieldReader"/>: a missing one binds to null and answers 422 at its path, never a crash.
    /// </summary>
    public sealed record CreateIncomeSourceBody(
        string DisplayName, string Kind, string Currency, string? Visibility, IReadOnlyList<OwnerBody> Owners,
        string? PayerLabel, string? Note, IReadOnlyList<IncomePhaseBody>? Phases)
    {
        internal CreateIncomeSourceCommand? ToCommand(FieldReader reader)
        {
            var visibility = reader.Visibility(Visibility, "visibility");
            if (!Nid.Core.Contracts.Currency.TryCreate(Currency, out var currency))
                reader.Add("currency", "invalid_currency", "A currency is a three-letter ISO 4217 code, in capitals.");
            var phases = (Phases ?? []).Select((p, i) => p is null ? Missing(reader, i) : p.Read(reader, $"phases[{i}]")).ToList();
            return reader.Ok
                ? new CreateIncomeSourceCommand(DisplayName, Kind, currency, visibility!.Value,
                    (Owners ?? []).Select(o => new OwnerShare(new PersonId(o.PersonId), o.ShareBp)).ToList(),
                    PayerLabel, Note, phases.Select(p => p!).ToList())
                : null;
        }

        // Nullable annotations stop at collection elements: a null in the phases array binds as such.
        private static PhaseDraft? Missing(FieldReader reader, int i)
        {
            reader.Add($"phases[{i}]", "request.required", "A phase is required.");
            return null;
        }
    }

    /// <summary>A phase as a body sends it; <c>netAmount</c> and <c>every</c> are guarded as <c>RequireMoney</c> does.</summary>
    public sealed record IncomePhaseBody(
        string Label, string StartsOn, string? EndsOn, MoneyBody? NetAmount, DurationBody? Every, MoneyBody? GrossAmount,
        int? MaxCycles, string? Note)
    {
        internal PhaseDraft? Read(FieldReader reader, string path)
        {
            var startsOn = reader.Date(StartsOn, path + ".startsOn");
            var endsOn = reader.Date(EndsOn, path + ".endsOn", required: false);
            var net = reader.Money(NetAmount, path + ".netAmount");
            var every = reader.Duration(Every, path + ".every");
            var gross = GrossAmount is null ? null : reader.Money(GrossAmount, path + ".grossAmount");
            return startsOn is { } s && net is { } n && every is { } e && (GrossAmount is null || gross is not null)
                ? new PhaseDraft(Label, s, endsOn, n, e, gross, MaxCycles, Note)
                : null;
        }
    }

    /// <summary>Only what is sent changes; <c>clear…</c> empties an optional field. The currency is not editable.</summary>
    public sealed record EditIncomeSourceBody(
        string? DisplayName, string? Kind, string? PayerLabel, bool ClearPayerLabel, string? Note, bool ClearNote)
    {
        public EditIncomeSourceCommand ToCommand(ItemId id) => new(id, DisplayName, Kind, new IncomeSourceChange(
            PayerLabel: PayerLabel, ClearPayerLabel: ClearPayerLabel, Note: Note, ClearNote: ClearNote));
    }
}
