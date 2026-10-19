using Nid.Core.Contracts;
using Nid.Income.Application;
using Nid.Income.Contracts;
using NodaTime;
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

        api.MapGet("/sources/{id:guid}", async (Guid id, string? from, string? to, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetIncomeSourceQuery(new ItemId(id), OptionalDate(from), OptionalDate(to)), ct)).ToHttp(detail => detail))
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

        api.MapPost("/sources/{id:guid}/phases", async (Guid id, IncomePhaseBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var draft = body.Read(reader, "");
                return draft is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new AddIncomePhaseCommand(new ItemId(id), draft), ct))
                        .ToHttp(phase => new IdResponse(phase), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPut("/sources/{id:guid}/phases/{phaseId:guid}", async (Guid id, Guid phaseId, IncomePhaseCorrectionBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(new ItemId(id), phaseId, reader);
                return command is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(command, ct)).ToHttp(phase => new IdResponse(phase));
            })
            .Returns<IdResponse>();

        api.MapDelete("/sources/{id:guid}/phases/{phaseId:guid}", async (Guid id, Guid phaseId, uint? version, IInvoker invoker, CancellationToken ct) =>
            {
                // A missing version answers 422 at `version`, as the correction's does: it would otherwise bind to 0.
                if (version is null)
                {
                    var reader = new FieldReader();
                    reader.Add("version", "request.required", "The version read with the phase is required.");
                    return reader.Problem();
                }
                // Written explicitly, as a person's removal: ToHttp would still write a JSON body, which a 204 must not carry.
                var result = await invoker.InvokeAsync(new DeleteIncomePhaseCommand(new ItemId(id), phaseId, version.Value), ct);
                return result.TryGetValue(out _) ? Results.NoContent() : result.ToHttp(v => v);
            })
            .ReturnsNoContent();

        api.MapPost("/sources/{id:guid}/phase-changes", async (Guid id, IncomePhaseChangeBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(new ItemId(id), reader);
                return command is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(command, ct)).ToHttp(phase => new IdResponse(phase), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapGet("/sources/{id:guid}/expected", async (Guid id, string from, string to, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetIncomeExpectedQuery(new ItemId(id), RequireDate(from), RequireDate(to)), ct)).ToHttp(e => e))
            .Returns<IncomeExpected>();

        api.MapPost("/sources/{id:guid}/one-offs", async (Guid id, IncomeOneOffBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToRecord(new ItemId(id), reader);
                return command is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(command, ct)).ToHttp(flow => new IdResponse(flow), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPut("/sources/{id:guid}/one-offs/{flowId:guid}", async (Guid id, Guid flowId, IncomeOneOffBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCorrection(new ItemId(id), flowId, reader);
                return command is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(command, ct)).ToHttp(flow => new IdResponse(flow));
            })
            .Returns<IdResponse>();

        api.MapDelete("/sources/{id:guid}/one-offs/{flowId:guid}", async (Guid id, Guid flowId, IInvoker invoker, CancellationToken ct) =>
            {
                // As a phase's removal: a 204 carries no body.
                var result = await invoker.InvokeAsync(new DeleteIncomeOneOffCommand(new ItemId(id), flowId), ct);
                return result.TryGetValue(out _) ? Results.NoContent() : result.ToHttp(v => v);
            })
            .ReturnsNoContent();

        api.MapPost("/sources/{id:guid}/close", async (Guid id, CloseIncomeSourceBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var endedOn = reader.Date(body.EndedOn, "endedOn");
                return endedOn is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new CloseIncomeSourceCommand(new ItemId(id), endedOn.Value), ct)).ToHttp(x => new IdResponse(x.Value));
            })
            .Returns<IdResponse>();

        api.MapPost("/sources/{id:guid}/reopen", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ReopenIncomeSourceCommand(new ItemId(id)), ct)).ToHttp(x => new IdResponse(x.Value)))
            .Returns<IdResponse>();

        return routes;
    }

    /// <summary>A one-off income: every member is read through <see cref="FieldReader"/> (a missing one binds to null).</summary>
    public sealed record IncomeOneOffBody(string? OccurredOn, MoneyBody? Amount, string? Note)
    {
        internal RecordIncomeOneOffCommand? ToRecord(ItemId source, FieldReader reader)
            => Read(reader) is var (on, amount) ? new RecordIncomeOneOffCommand(source, on, amount, Note) : null;

        internal CorrectIncomeOneOffCommand? ToCorrection(ItemId source, Guid flow, FieldReader reader)
            => Read(reader) is var (on, amount) ? new CorrectIncomeOneOffCommand(source, flow, on, amount, Note) : null;

        private (LocalDate On, Money Amount)? Read(FieldReader reader)
        {
            var on = reader.Date(OccurredOn, "occurredOn");
            var amount = reader.Money(Amount, "amount");
            return on is { } o && amount is { } a ? (o, a) : null;
        }
    }

    public sealed record CloseIncomeSourceBody(string? EndedOn);

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
            var startsOn = reader.Date(StartsOn, At(path, "startsOn"));
            var endsOn = reader.Date(EndsOn, At(path, "endsOn"), required: false);
            var net = reader.Money(NetAmount, At(path, "netAmount"));
            var every = reader.Duration(Every, At(path, "every"));
            var gross = GrossAmount is null ? null : reader.Money(GrossAmount, At(path, "grossAmount"));
            return startsOn is { } s && net is { } n && every is { } e && (GrossAmount is null || gross is not null)
                ? new PhaseDraft(Label, s, endsOn, n, e, gross, MaxCycles, Note)
                : null;
        }

        // A phase of the source's body sits under phases[i]; the phase routes' body is the phase itself.
        private static string At(string prefix, string field) => prefix == "" ? field : $"{prefix}.{field}";
    }

    /// <summary>
    /// A correction: every field of the phase, plus the <c>version</c> read with it. A missing version answers 422 at
    /// <c>version</c>: it would otherwise bind to 0 and pass for a conflict.
    /// </summary>
    public sealed record IncomePhaseCorrectionBody(
        string Label, string StartsOn, string? EndsOn, MoneyBody? NetAmount, DurationBody? Every, MoneyBody? GrossAmount,
        int? MaxCycles, string? Note, uint? Version)
    {
        internal EditIncomePhaseCommand? ToCommand(ItemId source, Guid phase, FieldReader reader)
        {
            var draft = new IncomePhaseBody(Label, StartsOn, EndsOn, NetAmount, Every, GrossAmount, MaxCycles, Note).Read(reader, "");
            if (Version is null)
                reader.Add("version", "request.required", "The version read with the phase is required.");
            return reader.Ok ? new EditIncomePhaseCommand(source, phase, draft!, Version!.Value) : null;
        }
    }

    /// <summary>A change of phase (spec §4): <c>every</c> is the old phase's when omitted.</summary>
    public sealed record IncomePhaseChangeBody(
        string? Label, string? EffectiveOn, MoneyBody? NetAmount, DurationBody? Every, MoneyBody? GrossAmount, string? Note)
    {
        internal ChangeIncomePhaseCommand? ToCommand(ItemId source, FieldReader reader)
        {
            var label = reader.Text(Label, "label");
            var effectiveOn = reader.Date(EffectiveOn, "effectiveOn");
            var net = reader.Money(NetAmount, "netAmount");
            var every = reader.Duration(Every, "every", required: false);
            var gross = GrossAmount is null ? null : reader.Money(GrossAmount, "grossAmount");
            return reader.Ok
                ? new ChangeIncomePhaseCommand(source, label!, effectiveOn!.Value, net!.Value, every, gross, Note)
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
