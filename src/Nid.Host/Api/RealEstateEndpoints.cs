using Nid.Core.Contracts;
using Nid.RealEstate.Application;
using Nid.RealEstate.Contracts;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Host.Api.SubscriptionEndpoints;

namespace Nid.Host.Api;

/// <summary>Spec 3A2 §4: <c>/api/v1/realestate</c>. Every body member is read through <see cref="FieldReader"/>: a missing one binds to null.</summary>
public static class RealEstateEndpoints
{
    public static IEndpointRouteBuilder MapRealEstateEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/realestate").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("/loans", async (string? scope, bool? ended, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListLoansQuery(ParseScope(scope), ended ?? false), ct)).ToHttp(list => list))
            .Returns<LoanList>();

        api.MapGet("/loans/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetLoanQuery(new ItemId(id)), ct)).ToHttp(detail => detail))
            .Returns<LoanDetail>();

        api.MapGet("/loans/{id:guid}/schedule", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetLoanScheduleQuery(new ItemId(id)), ct)).ToHttp(schedule => schedule))
            .Returns<LoanScheduleView>();

        api.MapPost("/loans/{id:guid}/simulations", async (Guid id, LoanSimulationBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var query = body.ToQuery(new ItemId(id), reader);
                return query is null ? reader.Problem() : (await invoker.InvokeAsync(query, ct)).ToHttp(simulation => simulation);
            })
            .Returns<LoanSimulationView>();

        api.MapPost("/loans", async (CreateLoanBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(reader);
                return command is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(command, ct)).ToHttp(id => new IdResponse(id.Value), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPatch("/loans/{id:guid}", async (Guid id, EditLoanBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(body.ToCommand(new ItemId(id)), ct)).ToHttp(x => new IdResponse(x.Value)))
            .Returns<IdResponse>();

        api.MapPut("/loans/{id:guid}/contract", async (Guid id, LoanContractBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(new ItemId(id), reader);
                return command is null ? reader.Problem() : (await invoker.InvokeAsync(command, ct)).ToHttp(x => new IdResponse(x.Value));
            })
            .Returns<IdResponse>();

        api.MapPost("/loans/{id:guid}/fees", async (Guid id, LoanFeeBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var fee = body.Read(reader);
                return fee is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new AddLoanFeeCommand(new ItemId(id), fee), ct))
                        .ToHttp(x => new IdResponse(x), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPut("/loans/{id:guid}/fees/{feeId:guid}", async (Guid id, Guid feeId, LoanFeeBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var fee = body.Read(reader);
                var version = RequireVersion(reader, body.Version);
                return fee is null || version is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new EditLoanFeeCommand(new ItemId(id), feeId, fee, version.Value), ct)).ToHttp(x => new IdResponse(x));
            })
            .Returns<IdResponse>();

        api.MapDelete("/loans/{id:guid}/fees/{feeId:guid}", async (Guid id, Guid feeId, uint? version, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                if (RequireVersion(reader, version) is not { } read)
                    return reader.Problem();
                // Written explicitly, as a phase's removal: ToHttp would still write a JSON body, which a 204 must not carry.
                var result = await invoker.InvokeAsync(new DeleteLoanFeeCommand(new ItemId(id), feeId, read), ct);
                return result.TryGetValue(out _) ? Results.NoContent() : result.ToHttp(x => x);
            })
            .ReturnsNoContent();

        api.MapPost("/loans/{id:guid}/repayments", async (Guid id, LoanRepaymentBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var repayment = body.Read(reader);
                return repayment is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new AddLoanRepaymentCommand(new ItemId(id), repayment), ct))
                        .ToHttp(x => new IdResponse(x), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPut("/loans/{id:guid}/repayments/{eventId:guid}", async (Guid id, Guid eventId, LoanRepaymentBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var repayment = body.Read(reader);
                var version = RequireVersion(reader, body.Version);
                return repayment is null || version is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new EditLoanRepaymentCommand(new ItemId(id), eventId, repayment, version.Value), ct))
                        .ToHttp(x => new IdResponse(x));
            })
            .Returns<IdResponse>();

        api.MapDelete("/loans/{id:guid}/repayments/{eventId:guid}", async (Guid id, Guid eventId, uint? version, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                if (RequireVersion(reader, version) is not { } read)
                    return reader.Problem();
                var result = await invoker.InvokeAsync(new DeleteLoanRepaymentCommand(new ItemId(id), eventId, read), ct);
                return result.TryGetValue(out _) ? Results.NoContent() : result.ToHttp(x => x);
            })
            .ReturnsNoContent();

        api.MapGet("/properties", async (string? scope, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListPropertiesQuery(ParseScope(scope)), ct)).ToHttp(list => list))
            .Returns<PropertyList>();

        api.MapGet("/properties/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetPropertyQuery(new ItemId(id)), ct)).ToHttp(detail => detail))
            .Returns<PropertyDetail>();

        api.MapPost("/properties", async (CreatePropertyBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(reader);
                return command is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(command, ct)).ToHttp(id => new IdResponse(id.Value), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPatch("/properties/{id:guid}", async (Guid id, EditPropertyBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(new ItemId(id), reader);
                return command is null ? reader.Problem() : (await invoker.InvokeAsync(command, ct)).ToHttp(x => new IdResponse(x.Value));
            })
            .Returns<IdResponse>();

        api.MapPost("/properties/{id:guid}/valuations", async (Guid id, PropertyValuationBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var asOf = reader.Date(body.AsOf, "asOf");
                var amount = LoanBodies.Number(reader, body.Amount, "amount");
                return !reader.Ok
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new RecordPropertyValuationCommand(new ItemId(id), asOf!.Value, amount!.Value, body.Note), ct))
                        .ToHttp(x => new IdResponse(x), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapDelete("/properties/{id:guid}/valuations/{valuationId:guid}", async (Guid id, Guid valuationId, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new VoidPropertyValuationCommand(new ItemId(id), valuationId), ct)).ToHttp(x => new IdResponse(x)))
            .Returns<IdResponse>();

        api.MapPost("/properties/{id:guid}/flows", async (Guid id, PropertyFlowBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var draft = body.Read(reader);
                return draft is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new RecordPropertyFlowCommand(new ItemId(id), draft), ct))
                        .ToHttp(x => new IdResponse(x), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPut("/properties/{id:guid}/flows/{flowId:guid}", async (Guid id, Guid flowId, PropertyFlowBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var draft = body.Read(reader);
                return draft is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new CorrectPropertyFlowCommand(new ItemId(id), flowId, draft), ct)).ToHttp(x => new IdResponse(x));
            })
            .Returns<IdResponse>();

        api.MapDelete("/properties/{id:guid}/flows/{flowId:guid}", async (Guid id, Guid flowId, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new VoidPropertyFlowCommand(new ItemId(id), flowId), ct)).ToHttp(x => new IdResponse(x)))
            .Returns<IdResponse>();

        api.MapPost("/properties/{id:guid}/recurring-costs", async (Guid id, PropertyRecurringCostBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var cost = body.Read(reader);
                return cost is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new AddPropertyRecurringCostCommand(new ItemId(id), cost), ct))
                        .ToHttp(x => new IdResponse(x), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPut("/properties/{id:guid}/recurring-costs/{costId:guid}", async (Guid id, Guid costId, PropertyRecurringCostBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var cost = body.Read(reader);
                var version = RequireVersion(reader, body.Version);
                return cost is null || version is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new EditPropertyRecurringCostCommand(new ItemId(id), costId, cost, version.Value), ct))
                        .ToHttp(x => new IdResponse(x));
            })
            .Returns<IdResponse>();

        api.MapDelete("/properties/{id:guid}/recurring-costs/{costId:guid}", async (Guid id, Guid costId, uint? version, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                if (RequireVersion(reader, version) is not { } read)
                    return reader.Problem();
                var result = await invoker.InvokeAsync(new DeletePropertyRecurringCostCommand(new ItemId(id), costId, read), ct);
                return result.TryGetValue(out _) ? Results.NoContent() : result.ToHttp(x => x);
            })
            .ReturnsNoContent();
        api.MapPost("/properties/{id:guid}/tenancies", async (Guid id, PropertyTenancyBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var tenancy = body.Read(reader);
                return tenancy is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new AddPropertyTenancyCommand(new ItemId(id), tenancy), ct))
                        .ToHttp(x => new IdResponse(x), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPut("/properties/{id:guid}/tenancies/{tenancyId:guid}", async (Guid id, Guid tenancyId, PropertyTenancyBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var tenancy = body.Read(reader);
                var version = RequireVersion(reader, body.Version);
                return tenancy is null || version is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new EditPropertyTenancyCommand(new ItemId(id), tenancyId, tenancy, version.Value), ct))
                        .ToHttp(x => new IdResponse(x));
            })
            .Returns<IdResponse>();

        api.MapDelete("/properties/{id:guid}/tenancies/{tenancyId:guid}", async (Guid id, Guid tenancyId, uint? version, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                if (RequireVersion(reader, version) is not { } read)
                    return reader.Problem();
                var result = await invoker.InvokeAsync(new DeletePropertyTenancyCommand(new ItemId(id), tenancyId, read), ct);
                return result.TryGetValue(out _) ? Results.NoContent() : result.ToHttp(x => x);
            })
            .ReturnsNoContent();

        return routes;
    }

    /// <summary>A missing version answers 422 at <c>version</c>: it would otherwise bind to 0 and pass for a conflict.</summary>
    private static uint? RequireVersion(FieldReader reader, uint? version)
    {
        if (version is null)
            reader.Add("version", "request.required", "The version read with it is required.");
        return version;
    }

    /// <summary>Spec §4: the visibility is required, with no default on the wire; the contract sits at the top level of the body.</summary>
    public sealed record CreateLoanBody(
        string? Name, string? Currency, string? Visibility, IReadOnlyList<OwnerBody>? Borrowers, string? Lender, string? Note,
        string? Principal, string? DisbursedOn, string? FirstPaymentDate, int? TotalPayments, string? Rounding, string? FirstPeriod,
        IReadOnlyList<LoanPhaseBody>? Phases, IReadOnlyList<LoanInsuranceBody>? Insurances, Guid? PropertyId)
    {
        internal CreateLoanCommand? ToCommand(FieldReader reader)
        {
            var visibility = reader.Visibility(Visibility, "visibility");
            if (!Nid.Core.Contracts.Currency.TryCreate(Currency, out var currency))
                reader.Add("currency", PersonCodes.UnknownCurrency, "A currency is a three-letter ISO 4217 code, in capitals.");
            var borrowers = LoanBodies.Borrowers(reader, Borrowers);
            var contract = LoanBodies.Contract(reader, Principal, DisbursedOn, FirstPaymentDate, TotalPayments, Rounding, FirstPeriod, Phases, Insurances);
            return reader.Ok
                ? new CreateLoanCommand(Name ?? "", currency, visibility!.Value, borrowers!, Lender, Note, contract!,
                    PropertyId is { } property ? new ItemId(property) : null)
                : null;
        }
    }

    /// <summary>
    /// Spec §4: name, lender, note and the linked property (spec 3C1 §4), only what is sent; <c>clear…</c> empties an optional
    /// field. Borrowers, visibility and currency are set at creation: the body has no such members (as an income source's).
    /// </summary>
    public sealed record EditLoanBody(string? Name, string? Lender, bool ClearLender, string? Note, bool ClearNote, Guid? PropertyId, bool ClearProperty)
    {
        public EditLoanCommand ToCommand(ItemId id) => new(id, Name, new LoanDetailsChange(Lender, ClearLender, Note, ClearNote,
            PropertyId is { } property ? new ItemId(property) : null, ClearProperty));
    }

    /// <summary>Spec §4: the whole contract and the <c>version</c> read with it.</summary>
    public sealed record LoanContractBody(
        uint? Version, string? Principal, string? DisbursedOn, string? FirstPaymentDate, int? TotalPayments, string? Rounding,
        string? FirstPeriod, IReadOnlyList<LoanPhaseBody>? Phases, IReadOnlyList<LoanInsuranceBody>? Insurances)
    {
        internal ReplaceLoanContractCommand? ToCommand(ItemId id, FieldReader reader)
        {
            var contract = LoanBodies.Contract(reader, Principal, DisbursedOn, FirstPaymentDate, TotalPayments, Rounding, FirstPeriod, Phases, Insurances);
            var version = RequireVersion(reader, Version);
            return contract is null || version is null ? null : new ReplaceLoanContractCommand(id, contract, version.Value);
        }
    }

    /// <summary>A phase; the rate and the amounts are invariant decimal strings (<c>"0.035"</c>, precision 1).</summary>
    public sealed record LoanPhaseBody(
        int? Seq, string? Mode, string? AnnualRate, string? RateType, int? Payments, string? PaymentOverride, string? OpeningBalanceOverride, string? Note);

    public sealed record LoanInsuranceBody(string? Mode, string? MonthlyAmount, string? AnnualRate, string? StartsOn, string? EndsOn, string? Note);

    /// <summary>A fee (precision 13): the <c>PUT</c> reads <c>version</c>, the <c>POST</c> ignores it.</summary>
    public sealed record LoanFeeBody(string? Kind, string? Amount, string? OnDate, string? Note, uint? Version)
    {
        internal FeeDraft? Read(FieldReader reader)
        {
            var kind = LoanBodies.Wire<FeeKind>(reader, Kind, "kind");
            var amount = LoanBodies.Number(reader, Amount, "amount");
            var onDate = reader.Date(OnDate, "onDate");
            return kind is null || amount is null || onDate is null ? null : new FeeDraft(kind.Value, amount.Value, onDate.Value, Note);
        }
    }

    /// <summary>An early repayment and its optional <c>penalty</c> (precision 13: the <c>PUT</c> reads <c>version</c>).</summary>
    public sealed record LoanRepaymentBody(string? OnDate, string? Amount, string? Effect, string? Penalty, string? Note, uint? Version)
    {
        internal RepaymentDraft? Read(FieldReader reader)
        {
            var onDate = reader.Date(OnDate, "onDate");
            var amount = LoanBodies.Number(reader, Amount, "amount");
            var effect = LoanBodies.Wire<RepaymentEffect>(reader, Effect, "effect");
            var penalty = LoanBodies.Number(reader, Penalty, "penalty", required: false);
            return onDate is null || amount is null || effect is null || (Penalty is not null && penalty is null)
                ? null
                : new RepaymentDraft(onDate.Value, amount.Value, effect.Value, penalty, Note);
        }
    }

    /// <summary>Spec §4: <c>{ on, amount, effect?, penalty? }</c>; no effect compares both.</summary>
    public sealed record LoanSimulationBody(string? On, string? Amount, string? Effect, string? Penalty)
    {
        internal SimulateLoanRepaymentQuery? ToQuery(ItemId id, FieldReader reader)
        {
            var on = reader.Date(On, "on");
            var amount = LoanBodies.Number(reader, Amount, "amount");
            var effect = Effect is null ? null : LoanBodies.Wire<RepaymentEffect>(reader, Effect, "effect");
            var penalty = LoanBodies.Number(reader, Penalty, "penalty", required: false);
            return reader.Ok ? new SimulateLoanRepaymentQuery(id, on!.Value, amount!.Value, effect, penalty ?? 0m) : null;
        }
    }

    /// <summary>Spec 3C1 §4: the visibility is required, with no default on the wire; the property's fields sit at the top level.</summary>
    public sealed record CreatePropertyBody(
        string? Name, string? Currency, string? Visibility, IReadOnlyList<OwnerBody>? Owners, string? Type, string? PurchaseDate,
        string? PurchasePrice, string? NotaryFees, string? AgencyFees, string? OtherAcquisitionCosts, string? SurfaceM2,
        string? LocationLabel, string? ValuationCadence, int? StaleAfterDays, string? RentalStartsOn, string? Note)
    {
        internal CreatePropertyCommand? ToCommand(FieldReader reader)
        {
            var visibility = reader.Visibility(Visibility, "visibility");
            if (!Nid.Core.Contracts.Currency.TryCreate(Currency, out var currency))
                reader.Add("currency", PersonCodes.UnknownCurrency, "A currency is a three-letter ISO 4217 code, in capitals.");
            var owners = LoanBodies.Borrowers(reader, Owners, "owners");
            var type = PropertyBodies.Wire<PropertyType>(reader, Type, "type", required: true);
            var purchaseDate = reader.Date(PurchaseDate, "purchaseDate");
            var price = LoanBodies.Number(reader, PurchasePrice, "purchasePrice");
            var notary = LoanBodies.Number(reader, NotaryFees, "notaryFees", required: false);
            var agency = LoanBodies.Number(reader, AgencyFees, "agencyFees", required: false);
            var other = LoanBodies.Number(reader, OtherAcquisitionCosts, "otherAcquisitionCosts", required: false);
            var surface = LoanBodies.Number(reader, SurfaceM2, "surfaceM2", required: false);
            var cadence = PropertyBodies.Wire<PropertyCadence>(reader, ValuationCadence, "valuationCadence", required: false);
            var rentalStartsOn = reader.Date(RentalStartsOn, "rentalStartsOn", required: false);
            return reader.Ok
                ? new CreatePropertyCommand(Name ?? "", currency, visibility!.Value, owners!, new PropertyDetails(
                    type!.Value, purchaseDate!.Value, price!.Value, notary ?? 0m, agency ?? 0m, other ?? 0m, surface, LocationLabel,
                    cadence ?? PropertyCadence.Yearly, StaleAfterDays, rentalStartsOn, Note))
                : null;
        }
    }

    /// <summary>
    /// Spec 3C1 §4: the <c>version</c> read and only what changes; <c>clear…</c> empties an optional field. Owners, visibility and
    /// currency are set at creation: the body has no such members.
    /// </summary>
    public sealed record EditPropertyBody(
        uint? Version, string? Name, string? Type, string? PurchaseDate, string? PurchasePrice, string? NotaryFees, string? AgencyFees,
        string? OtherAcquisitionCosts, string? SurfaceM2, bool ClearSurfaceM2, string? LocationLabel, bool ClearLocationLabel,
        string? ValuationCadence, int? StaleAfterDays, bool ClearStaleAfterDays, string? RentalStartsOn, bool ClearRentalStartsOn,
        string? Note, bool ClearNote)
    {
        internal EditPropertyCommand? ToCommand(ItemId id, FieldReader reader)
        {
            var version = RequireVersion(reader, Version);
            var change = new PropertyChange(
                PropertyBodies.Wire<PropertyType>(reader, Type, "type", required: false),
                reader.Date(PurchaseDate, "purchaseDate", required: false),
                LoanBodies.Number(reader, PurchasePrice, "purchasePrice", required: false),
                LoanBodies.Number(reader, NotaryFees, "notaryFees", required: false),
                LoanBodies.Number(reader, AgencyFees, "agencyFees", required: false),
                LoanBodies.Number(reader, OtherAcquisitionCosts, "otherAcquisitionCosts", required: false),
                LoanBodies.Number(reader, SurfaceM2, "surfaceM2", required: false), ClearSurfaceM2,
                LocationLabel, ClearLocationLabel,
                PropertyBodies.Wire<PropertyCadence>(reader, ValuationCadence, "valuationCadence", required: false),
                StaleAfterDays, ClearStaleAfterDays,
                reader.Date(RentalStartsOn, "rentalStartsOn", required: false), ClearRentalStartsOn,
                Note, ClearNote);
            return reader.Ok ? new EditPropertyCommand(id, Name, change, version!.Value) : null;
        }
    }

    /// <summary>Spec 3C1 §4: an estimate, under the envelopes' member names; the amount an invariant decimal string (precision 1).</summary>
    public sealed record PropertyValuationBody(string? AsOf, string? Amount, string? Note);

    /// <summary>Spec 3C1 §4: <c>{ occurredOn, kind, amount, category, note? }</c>; kind and category are the module's convention, checked by the handler.</summary>
    public sealed record PropertyFlowBody(string? OccurredOn, string? Kind, string? Amount, string? Category, string? Note)
    {
        internal PropertyFlowDraft? Read(FieldReader reader)
        {
            var occurredOn = reader.Date(OccurredOn, "occurredOn");
            var amount = LoanBodies.Number(reader, Amount, "amount");
            return occurredOn is null || amount is null ? null : new PropertyFlowDraft(occurredOn.Value, Kind, amount.Value, Category, Note);
        }
    }

    /// <summary>
    /// Spec 3C2 §3: a planned cost, <c>every</c> the house <c>{ count, unit }</c>; the <c>PUT</c> reads <c>version</c>, the <c>POST</c>
    /// ignores it (as a loan's fee). The category and the period's bounds are the handler's (precisions 3–4).
    /// </summary>
    public sealed record PropertyRecurringCostBody(
        string? Category, string? Amount, DurationBody? Every, string? StartsOn, string? EndsOn, string? Note, uint? Version)
    {
        internal RecurringCostDraft? Read(FieldReader reader)
        {
            var amount = LoanBodies.Number(reader, Amount, "amount");
            if (Every is null)
                reader.Add("every", "request.required", "A period is required.");
            var unit = Every is null ? null : PropertyBodies.Wire<DurationUnit>(reader, Every.Unit, "every.unit", required: true);
            var startsOn = reader.Date(StartsOn, "startsOn");
            var endsOn = reader.Date(EndsOn, "endsOn", required: false);
            return reader.Ok ? new RecurringCostDraft(Category, amount!.Value, Every!.Count, unit!.Value, startsOn!.Value, endsOn, Note) : null;
        }
    }

    /// <summary>
    /// Spec 3C2 §3: a tenancy; a missing label reaches the rules as empty and is refused there, at <c>label</c>. The <c>PUT</c> reads
    /// <c>version</c>, the <c>POST</c> ignores it.
    /// </summary>
    public sealed record PropertyTenancyBody(
        string? Label, string? StartsOn, string? EndsOn, string? RentMonthly, string? ChargesMonthly, string? Deposit, string? Note, uint? Version)
    {
        internal Tenancy? Read(FieldReader reader)
        {
            var startsOn = reader.Date(StartsOn, "startsOn");
            var endsOn = reader.Date(EndsOn, "endsOn", required: false);
            var rent = LoanBodies.Number(reader, RentMonthly, "rentMonthly");
            var charges = LoanBodies.Number(reader, ChargesMonthly, "chargesMonthly", required: false);
            var deposit = LoanBodies.Number(reader, Deposit, "deposit", required: false);
            return reader.Ok ? new Tenancy(Label ?? "", startsOn!.Value, endsOn, rent!.Value, charges ?? 0m, deposit, Note) : null;
        }
    }

    internal static class PropertyBodies
    {
        /// <summary>A property enum in snake_case (<see cref="LoanWire"/>); a value sent outside the list is the module's refusal at its field.</summary>
        public static T? Wire<T>(FieldReader reader, string? value, string path, bool required) where T : struct, Enum
        {
            if (value is null)
            {
                if (required)
                    reader.Add(path, "request.required", "This field is required.");
                return null;
            }
            if (LoanWire.TryParse<T>(value, out var parsed))
                return parsed;
            reader.Add(path, PropertyCodes.InvalidPropertyField, $"'{value}' is not one of {string.Join(", ", Enum.GetValues<T>().Select(v => LoanWire.Of(v)))}.");
            return null;
        }
    }

    /// <summary>The readers the loan bodies share; each refusal is added to the reader at its body path.</summary>
    internal static class LoanBodies
    {
        public static decimal? Number(FieldReader reader, string? value, string path, bool required = true)
        {
            if (value is null)
            {
                if (required)
                    reader.Add(path, "request.required", "This amount is required.");
                return null;
            }
            return reader.Decimal(value, path);
        }

        /// <summary>A loans.md enum in snake_case (<see cref="LoanWire"/>); anything else is the module's refusal at its field.</summary>
        public static T? Wire<T>(FieldReader reader, string? value, string path) where T : struct, Enum
        {
            // Spec 3B1 §4.3: a missing enum is missing, as a missing amount or date is; only a value sent can be unknown.
            if (value is null)
            {
                reader.Add(path, "request.required", "This field is required.");
                return null;
            }
            if (LoanWire.TryParse<T>(value, out var parsed))
                return parsed;
            reader.Add(path, LoanCodes.InvalidLoan, $"'{value}' is not one of {string.Join(", ", Enum.GetValues<T>().Select(v => LoanWire.Of(v)))}.");
            return null;
        }

        /// <summary>A loan's borrowers, or a property's owners at <paramref name="path"/> (set at creation, never sent again); at least one.</summary>
        public static IReadOnlyList<OwnerShare>? Borrowers(FieldReader reader, IReadOnlyList<OwnerBody>? body, string path = "borrowers")
        {
            if (body is null || body.Count == 0)
            {
                reader.Add(path, "request.required", "At least one owner is required.");
                return null;
            }
            for (var i = 0; i < body.Count; i++)
                if (body[i] is null) // nullable annotations stop at collection elements
                    reader.Add($"{path}[{i}]", "request.required", "An owner is required.");
            return body.Where(o => o is not null).Select(o => new OwnerShare(new PersonId(o.PersonId), o.ShareBp)).ToList();
        }

        public static LoanContract? Contract(
            FieldReader reader, string? principal, string? disbursedOn, string? firstPaymentDate, int? totalPayments, string? rounding,
            string? firstPeriod, IReadOnlyList<LoanPhaseBody>? phases, IReadOnlyList<LoanInsuranceBody>? insurances)
        {
            var amount = Number(reader, principal, "principal");
            var disbursed = reader.Date(disbursedOn, "disbursedOn");
            var first = reader.Date(firstPaymentDate, "firstPaymentDate");
            if (totalPayments is null)
                reader.Add("totalPayments", "request.required", "The number of payments is required.");
            var roundingMode = Wire<InterestRounding>(reader, rounding, "rounding");
            var firstPeriodMode = Wire<FirstPeriod>(reader, firstPeriod, "firstPeriod");
            var phaseEntries = (phases ?? []).Select((p, i) => Phase(reader, p, $"phases[{i}]")).ToList();
            var insuranceEntries = (insurances ?? []).Select((x, i) => Insurance(reader, x, $"insurances[{i}]")).ToList();
            return reader.Ok
                ? new LoanContract(amount!.Value, disbursed!.Value, first!.Value, totalPayments!.Value, roundingMode!.Value, firstPeriodMode!.Value,
                    phaseEntries.Select(p => p!).ToList(), insuranceEntries.Select(x => x!).ToList())
                : null;
        }

        private static LoanPhaseEntry? Phase(FieldReader reader, LoanPhaseBody? body, string path)
        {
            if (body is null)
            {
                reader.Add(path, "request.required", "A phase is required.");
                return null;
            }
            if (body.Seq is null)
                reader.Add(path + ".seq", "request.required", "A phase's seq is required.");
            var mode = Wire<PhaseMode>(reader, body.Mode, path + ".mode");
            var rate = Number(reader, body.AnnualRate, path + ".annualRate");
            var rateType = Wire<RateType>(reader, body.RateType, path + ".rateType");
            var payment = Number(reader, body.PaymentOverride, path + ".paymentOverride", required: false);
            var opening = Number(reader, body.OpeningBalanceOverride, path + ".openingBalanceOverride", required: false);
            return body.Seq is null || mode is null || rate is null || rateType is null
                ? null
                : new LoanPhaseEntry(new LoanPhase(body.Seq.Value, mode.Value, rate.Value, rateType.Value, body.Payments, payment, opening), body.Note);
        }

        private static LoanInsuranceEntry? Insurance(FieldReader reader, LoanInsuranceBody? body, string path)
        {
            if (body is null)
            {
                reader.Add(path, "request.required", "An insurance is required.");
                return null;
            }
            var mode = Wire<InsuranceMode>(reader, body.Mode, path + ".mode");
            var monthly = Number(reader, body.MonthlyAmount, path + ".monthlyAmount", required: false);
            var rate = Number(reader, body.AnnualRate, path + ".annualRate", required: false);
            var startsOn = reader.Date(body.StartsOn, path + ".startsOn");
            var endsOn = reader.Date(body.EndsOn, path + ".endsOn", required: false);
            return mode is null || startsOn is null
                ? null
                : new LoanInsuranceEntry(new LoanInsurance(mode.Value, monthly, rate, startsOn.Value, endsOn), body.Note);
        }
    }
}
