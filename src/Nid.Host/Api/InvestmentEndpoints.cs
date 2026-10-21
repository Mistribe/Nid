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

        api.MapGet("/routine", async (string? on, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetRoutineQuery(OptionalDate(on)), ct)).ToHttp(view => view))
            .Returns<RoutineView>();

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

        api.MapPost("/routine", async (RoutineBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(body.ToCommand(), ct)).ToHttp(recap => recap))
            .Returns<RoutineRecap>();

        api.MapPost("/routine/undo", async (RoutineUndoBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new UndoRoutineCommand((body.Facts ?? [])
                    .Select(f => new RoutineFact(new ItemId(f.EnvelopeId), f.ValuationId, f.FlowId)).ToList()), ct))
                .ToHttp(undone => undone))
            .Returns<RoutineUndone>();

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

        api.MapGet("/envelopes/{id:guid}/holdings", async (Guid id, string? on, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetHoldingsQuery(new ItemId(id), OptionalDate(on)), ct)).ToHttp(v => v))
            .Returns<HoldingsView>();

        api.MapPost("/envelopes/{id:guid}/holdings", async (Guid id, HoldingBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var draft = body.Read(reader);
                return draft is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new AddHoldingCommand(new ItemId(id), draft), ct)).ToHttp(x => new IdResponse(x), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPut("/envelopes/{id:guid}/holdings/{holdingId:guid}", async (Guid id, Guid holdingId, HoldingBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var draft = body.Read(reader);
                var version = RequireVersion(reader, body.Version);
                return draft is null || version is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new EditHoldingCommand(new ItemId(id), holdingId, draft, version.Value), ct)).ToHttp(x => new IdResponse(x));
            })
            .Returns<IdResponse>();

        api.MapDelete("/envelopes/{id:guid}/holdings/{holdingId:guid}", async (Guid id, Guid holdingId, uint? version, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                if (RequireVersion(reader, version) is not { } read)
                    return reader.Problem();
                var result = await invoker.InvokeAsync(new DeleteHoldingCommand(new ItemId(id), holdingId, read), ct);
                return result.TryGetValue(out _) ? Results.NoContent() : result.ToHttp(x => x);
            })
            .ReturnsNoContent();

        api.MapGet("/envelopes/{id:guid}/holdings/{holdingId:guid}/movements", async (Guid id, Guid holdingId, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListHoldingMovementsQuery(new ItemId(id), holdingId), ct)).ToHttp(v => v))
            .Returns<HoldingMovementList>();

        api.MapPost("/envelopes/{id:guid}/holdings/{holdingId:guid}/movements", async (Guid id, Guid holdingId, MovementBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var draft = body.Read(reader);
                return draft is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new AddHoldingMovementCommand(new ItemId(id), holdingId, draft), ct)).ToHttp(x => x, StatusCodes.Status201Created);
            })
            .Returns<MovementRecorded>(StatusCodes.Status201Created);

        api.MapPost("/envelopes/{id:guid}/switches", async (Guid id, SwitchBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var draft = body.Read(reader);
                return draft is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new RecordSwitchCommand(new ItemId(id), draft), ct)).ToHttp(x => x, StatusCodes.Status201Created);
            })
            .Returns<SwitchRecorded>(StatusCodes.Status201Created);

        api.MapGet("/envelopes/{id:guid}/holdings/{holdingId:guid}/series", async (Guid id, Guid holdingId, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetHoldingSeriesQuery(new ItemId(id), holdingId), ct)).ToHttp(v => v))
            .Returns<HoldingSeries>();

        api.MapGet("/envelopes/{id:guid}/holdings/{holdingId:guid}/performance", async (Guid id, Guid holdingId, string from, string to, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetHoldingPerformanceQuery(new ItemId(id), holdingId, RequireDate(from), RequireDate(to)), ct)).ToHttp(p => p))
            .Returns<PeriodView>();

        api.MapGet("/envelopes/{id:guid}/holdings/{holdingId:guid}/cost-reports", async (Guid id, Guid holdingId, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListCostReportsQuery(new ItemId(id), holdingId), ct)).ToHttp(v => v))
            .Returns<CostReportList>();

        api.MapGet("/envelopes/{id:guid}/holdings/{holdingId:guid}/cost-reports/{year:int}", async (Guid id, Guid holdingId, int year, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetCostReportQuery(new ItemId(id), holdingId, year), ct)).ToHttp(v => v))
            .Returns<CostReportView>();

        api.MapPut("/envelopes/{id:guid}/holdings/{holdingId:guid}/cost-reports/{year:int}", async (Guid id, Guid holdingId, int year, CostReportBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new SaveCostReportCommand(new ItemId(id), holdingId, year, body.ToDraft()), ct)).ToHttp(v => v))
            .Returns<CostReportView>();

        api.MapGet("/envelopes/{id:guid}/holding-snapshots", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListHoldingsSnapshotsQuery(new ItemId(id)), ct)).ToHttp(v => v))
            .Returns<HoldingSnapshotList>();

        api.MapPost("/envelopes/{id:guid}/holding-snapshots", async (Guid id, SnapshotBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var draft = body.Read(reader);
                return draft is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new RecordHoldingsSnapshotCommand(new ItemId(id), null, draft), ct)).ToHttp(x => x, StatusCodes.Status201Created);
            })
            .Returns<SnapshotRecorded>(StatusCodes.Status201Created);

        api.MapPut("/envelopes/{id:guid}/holding-snapshots/{snapshotId:guid}", async (Guid id, Guid snapshotId, SnapshotBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var draft = body.Read(reader);
                return draft is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new RecordHoldingsSnapshotCommand(new ItemId(id), snapshotId, draft), ct)).ToHttp(x => x);
            })
            .Returns<SnapshotRecorded>();

        api.MapDelete("/envelopes/{id:guid}/holding-snapshots/{snapshotId:guid}", async (Guid id, Guid snapshotId, IInvoker invoker, CancellationToken ct) =>
            {
                var result = await invoker.InvokeAsync(new DeleteHoldingsSnapshotCommand(new ItemId(id), snapshotId), ct);
                return result.TryGetValue(out _) ? Results.NoContent() : result.ToHttp(x => x);
            })
            .ReturnsNoContent();

        return routes;
    }

    private static uint? RequireVersion(FieldReader reader, uint? version)
    {
        if (version is null)
            reader.Add("version", "request.required", "The version read with it is required.");
        return version;
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
        string? Note, bool ClearNote, decimal? UcShareFloorPct, bool? ClearUcShareFloorPct)
    {
        public EditEnvelopeCommand ToCommand(ItemId id) => new(id, DisplayName, Kind, new EnvelopeChange(
            Institution: Institution, ClearInstitution: ClearInstitution, AccountHint: AccountHint, ClearAccountHint: ClearAccountHint,
            OpenedOn: OptionalDate(OpenedOn), ClearOpenedOn: ClearOpenedOn, StaleAfterDays: StaleAfterDays,
            ClearStaleAfterDays: ClearStaleAfterDays, Note: Note, ClearNote: ClearNote, UcShareFloorPct: UcShareFloorPct,
            ClearUcShareFloorPct: ClearUcShareFloorPct ?? false), ValuationCadence);
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

    public sealed record RoutineLineBody(Guid EnvelopeId, MoneyBody? Value, MoneyBody? Contribution);

    public sealed record RoutineBody(string On, IReadOnlyList<RoutineLineBody>? Lines)
    {
        public RecordRoutineCommand ToCommand() => new(RequireDate(On), (Lines ?? [])
            .Select(l => new RoutineEntry(new ItemId(l.EnvelopeId), l.Value?.ToMoney(), l.Contribution?.ToMoney())).ToList());
    }

    public sealed record RoutineFactBody(Guid EnvelopeId, Guid? ValuationId, Guid? FlowId);

    public sealed record RoutineUndoBody(IReadOnlyList<RoutineFactBody>? Facts);

    public sealed record CloseBody(string ClosedOn, MoneyBody? FinalWithdrawal);

    /// <summary>
    /// Holdings spec §2.1: decimals are strings, dates ISO; name and kind are checked by the rules, at their fields. The <c>PUT</c>
    /// reads <c>version</c>, the <c>POST</c> ignores it.
    /// </summary>
    public sealed record HoldingBody(
        string? Name, string? Isin, string? Kind, string? AssetClass, string? Issuer, int? RiskIndicator,
        string? OpenedOn, string? MaturesOn, string? EntryCostPct, string? ClosedOn, string? Note, uint? Version)
    {
        internal HoldingDraft? Read(FieldReader reader)
        {
            var openedOn = reader.Date(OpenedOn, "openedOn", required: false);
            var maturesOn = reader.Date(MaturesOn, "maturesOn", required: false);
            var closedOn = reader.Date(ClosedOn, "closedOn", required: false);
            var entryCost = reader.Decimal(EntryCostPct, "entryCostPct");
            return reader.Ok ? new HoldingDraft(Name, Isin, Kind, AssetClass, Issuer, RiskIndicator, openedOn, maturesOn, entryCost, closedOn, Note) : null;
        }
    }

    public sealed record SnapshotLineBody(Guid HoldingId, string? Units, string? UnitPrice, MoneyBody? Value, string? CostBasisPerUnit);

    /// <summary>Holdings spec §2.2 and D7: <c>complete</c> defaults to true; <c>recordValuation</c> also records the total as the envelope's value.</summary>
    public sealed record SnapshotBody(string? AsOf, string? SourceLabel, bool? Complete, IReadOnlyList<SnapshotLineBody>? Lines, bool RecordValuation)
    {
        internal SnapshotDraft? Read(FieldReader reader)
        {
            var asOf = reader.Date(AsOf, "asOf");
            var lines = new List<SnapshotLineDraft>();
            for (var i = 0; i < (Lines ?? []).Count; i++)
            {
                var l = Lines![i];
                var value = reader.Money(l.Value, $"lines[{i}].value");
                var units = reader.Decimal(l.Units, $"lines[{i}].units");
                var price = reader.Decimal(l.UnitPrice, $"lines[{i}].unitPrice");
                var basis = reader.Decimal(l.CostBasisPerUnit, $"lines[{i}].costBasisPerUnit");
                if (value is { } v)
                    lines.Add(new SnapshotLineDraft(l.HoldingId, units, price, v, basis));
            }
            return reader.Ok ? new SnapshotDraft(asOf!.Value, SourceLabel, Complete ?? true, lines, RecordValuation) : null;
        }
    }

    /// <summary>Issue #34 §2.5: percentages as numbers, as stated on the annual document; the year is the route's.</summary>
    public sealed record CostReportBody(
        decimal? GrossReturnPct, decimal? TotalFeesPct, decimal? NetReturnPct, decimal? ContractFeesPct, decimal? SupportFeesPct,
        decimal? RetrocessionPct, decimal? NetAnnualized5yPct, string? SourceLabel)
    {
        public CostReportDraft ToDraft() => new(GrossReturnPct, TotalFeesPct, NetReturnPct, ContractFeesPct, SupportFeesPct, RetrocessionPct,
            NetAnnualized5yPct, SourceLabel);
    }

    /// <summary>Issue #34 §2.3: units are signed by the kind (bought +, sold or deducted −); decimals are strings, dates ISO.</summary>
    public sealed record MovementBody(
        string? OccurredOn, string? Kind, string? Units, string? UnitPrice, MoneyBody? Amount, Guid? EnvelopeFlowId, string? Label, string? ExternalRef)
    {
        internal MovementDraft? Read(FieldReader reader)
        {
            var on = reader.Date(OccurredOn, "occurredOn");
            var amount = reader.Money(Amount, "amount");
            var units = reader.Decimal(Units, "units");
            var price = reader.Decimal(UnitPrice, "unitPrice");
            return reader.Ok ? new MovementDraft(on!.Value, Kind, units, price, amount!.Value, EnvelopeFlowId, Label, ExternalRef) : null;
        }
    }

    public sealed record SwitchLegBody(Guid HoldingId, MoneyBody? Amount, string? Units, string? UnitPrice)
    {
        internal SwitchLegDraft? Read(FieldReader reader, string at)
        {
            var amount = reader.Money(Amount, $"{at}.amount");
            var units = reader.Decimal(Units, $"{at}.units");
            var price = reader.Decimal(UnitPrice, $"{at}.unitPrice");
            return amount is { } a ? new SwitchLegDraft(HoldingId, a, units, price) : null;
        }
    }

    /// <summary>
    /// A switch between holdings, every leg at once (issue #34 §2.3, #45): one or more <c>from</c> legs, one or more <c>to</c> legs, each as
    /// printed on the notice. A lone object still reads as one leg. <c>maturity</c> makes the <c>from</c> legs maturities (a structured
    /// product reaching its term or called) rather than <c>switch_out</c>. A switch fee is a separate <c>fee</c> movement.
    /// </summary>
    public sealed record SwitchBody(string? OccurredOn, bool Maturity, IReadOnlyList<SwitchLegBody>? From, IReadOnlyList<SwitchLegBody>? To, string? Label)
    {
        internal SwitchDraft? Read(FieldReader reader)
        {
            var on = reader.Date(OccurredOn, "occurredOn");
            if (From is not { Count: > 0 })
                reader.Add("from", "request.required", "At least one holding the money leaves is required.");
            if (To is not { Count: > 0 })
                reader.Add("to", "request.required", "At least one holding the money goes to is required.");
            var from = (From ?? []).Select((l, i) => l.Read(reader, $"from[{i}]")).ToList();
            var to = (To ?? []).Select((l, i) => l.Read(reader, $"to[{i}]")).ToList();
            return reader.Ok ? new SwitchDraft(on!.Value, Maturity, from!, to!, Label) : null;
        }
    }
}
