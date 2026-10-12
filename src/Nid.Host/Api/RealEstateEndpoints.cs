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
        IReadOnlyList<LoanPhaseBody>? Phases, IReadOnlyList<LoanInsuranceBody>? Insurances)
    {
        internal CreateLoanCommand? ToCommand(FieldReader reader)
        {
            var visibility = reader.Visibility(Visibility, "visibility");
            if (!Nid.Core.Contracts.Currency.TryCreate(Currency, out var currency))
                reader.Add("currency", PersonCodes.UnknownCurrency, "A currency is a three-letter ISO 4217 code, in capitals.");
            var borrowers = LoanBodies.Borrowers(reader, Borrowers);
            var contract = LoanBodies.Contract(reader, Principal, DisbursedOn, FirstPaymentDate, TotalPayments, Rounding, FirstPeriod, Phases, Insurances);
            return reader.Ok
                ? new CreateLoanCommand(Name ?? "", currency, visibility!.Value, borrowers!, Lender, Note, contract!)
                : null;
        }
    }

    /// <summary>
    /// Spec §4: name, lender and note, only what is sent; <c>clear…</c> empties an optional field. Borrowers, visibility and
    /// currency are set at creation: the body has no such members (as an income source's).
    /// </summary>
    public sealed record EditLoanBody(string? Name, string? Lender, bool ClearLender, string? Note, bool ClearNote)
    {
        public EditLoanCommand ToCommand(ItemId id) => new(id, Name, new LoanDetailsChange(Lender, ClearLender, Note, ClearNote));
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

        /// <summary>The creation's borrowers (precision 8: never sent again); at least one.</summary>
        public static IReadOnlyList<OwnerShare>? Borrowers(FieldReader reader, IReadOnlyList<OwnerBody>? body)
        {
            if (body is null || body.Count == 0)
            {
                reader.Add("borrowers", "request.required", "A loan has at least one borrower.");
                return null;
            }
            for (var i = 0; i < body.Count; i++)
                if (body[i] is null) // nullable annotations stop at collection elements
                    reader.Add($"borrowers[{i}]", "request.required", "A borrower is required.");
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
