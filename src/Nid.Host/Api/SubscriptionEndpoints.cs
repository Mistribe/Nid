using Nid.Core.Contracts;
using Nid.Subscriptions.Application;
using Nid.Subscriptions.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Synapse.Abstractions;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Host.Api;

public static class SubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/subscriptions").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("", async (string? scope, string? from, string? to, string? horizon, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(
                    new ListSubscriptionsQuery(
                        ParseScope(scope), OptionalDate(from), OptionalDate(to), OptionalDate(horizon)), ct))
                .ToHttp(list => list))
            .Returns<SubscriptionList>();

        api.MapGet("/{id:guid}", async (Guid id, string? from, string? to, string? horizon, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(
                    new GetSubscriptionQuery(
                        new ItemId(id), OptionalDate(from), OptionalDate(to), OptionalDate(horizon)), ct))
                .ToHttp(detail => detail))
            .Returns<SubscriptionDetail>();

        // Create / open phase / add purchase were implemented early because Task 4's own acceptance test
        // creates its fixtures through the real HTTP API — see the comment atop WriteUseCases.cs.
        api.MapPost("", async (CreateSubscriptionBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(reader);
                return command is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(command, ct)).ToHttp(id => new IdResponse(id.Value), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPatch("/{id:guid}", async (Guid id, EditSubscriptionBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(body.ToCommand(new ItemId(id)), ct)).ToHttp(x => new IdResponse(x.Value)))
            .Returns<IdResponse>();

        api.MapPost("/{id:guid}/phases", async (Guid id, PhaseBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(body.ToOpenCommand(new ItemId(id)), ct))
                .ToHttp(phase => new IdResponse(phase.Value), StatusCodes.Status201Created))
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPatch("/{id:guid}/phases/{phaseId:guid}", async (Guid id, Guid phaseId, ClosePhaseBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(
                    new ClosePhaseCommand(new ItemId(id), new BillingPhaseId(phaseId), RequireDate(body.EndsOn)), ct))
                .ToHttp(phase => new IdResponse(phase.Value)))
            .Returns<IdResponse>();

        api.MapPost("/{id:guid}/price-changes", async (Guid id, PriceChangeBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(new ItemId(id), reader);
                return command is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(command, ct)).ToHttp(phase => new IdResponse(phase.Value), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPost("/{id:guid}/purchases", async (Guid id, PurchaseInput body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var draft = body.Read(reader, "");
                return draft is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new AddPurchaseCommand(new ItemId(id), draft), ct))
                        .ToHttp(purchase => new IdResponse(purchase.Value), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapDelete("/{id:guid}/purchases/{purchaseId:guid}", async (Guid id, Guid purchaseId, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new DeletePurchaseCommand(new ItemId(id), new PurchaseId(purchaseId)), ct))
                .ToHttp(purchase => new IdResponse(purchase.Value)))
            .Returns<IdResponse>();

        return routes;
    }

    /// <summary>
    /// Household is the default, matching the header selector of frontend/screens.md §13 — but only when the
    /// caller asked for no scope at all. A scope nobody can read is a bad request: answering the household
    /// question to someone who asked a different one, with a 200, is how a wrong number looks right.
    /// </summary>
    internal static VisibilityScope ParseScope(string? scope) => scope switch
    {
        null => VisibilityScope.Household,
        _ when string.Equals(scope, "me", StringComparison.OrdinalIgnoreCase) => VisibilityScope.Me,
        _ when string.Equals(scope, "household", StringComparison.OrdinalIgnoreCase) => VisibilityScope.Household,
        _ => throw new BadHttpRequestException($"'{scope}' is not a scope: send 'household' or 'me'."),
    };

    /// <summary>
    /// Absent stays absent, and keeps the documented default. Present-but-unreadable is a bad request, never
    /// a silent fallback to that same default: the caller would be handed a different window, a different
    /// horizon or a dropped cancellation and told it worked.
    /// </summary>
    internal static LocalDate? OptionalDate(string? value)
        => value is null ? null : RequireDate(value);

    internal static LocalDate RequireDate(string value)
        => LocalDatePattern.Iso.Parse(value) is { Success: true } parsed
            ? parsed.Value
            : throw new BadHttpRequestException($"'{value}' is not an ISO date (yyyy-mm-dd).");

    public sealed record IdResponse(Guid Id);

    public sealed record MoneyBody(string Amount, string Currency)
    {
        public Money ToMoney()
            => Money.TryParse(Amount, Currency, out var money)
                ? money
                : throw new BadHttpRequestException(
                    "An amount is a decimal string such as \"12.99\" (at most four decimals) with a valid ISO 4217 currency.");
    }

    public sealed record DurationBody(int Count, string Unit)
    {
        public Duration ToDuration()
            => Enum.TryParse<DurationUnit>(Unit, ignoreCase: true, out var unit)
               && Duration.Create(Count, unit).TryGetValue(out var duration)
                ? duration
                : throw new BadHttpRequestException("A positive count and a unit of day, week, month or year are required.");
    }

    public sealed record PayerBody(string Type, Guid? PersonId)
    {
        public Payer ToPayer()
            => Enum.TryParse<PayerType>(Type, ignoreCase: true, out var type)
                ? new Payer(type, PersonId is { } p ? new Core.Contracts.PersonId(p) : null)
                : throw new BadHttpRequestException("A payer type of person, household or external is required.");
    }

    public sealed record OwnerBody(Guid PersonId, int ShareBp);

    public sealed record CreateSubscriptionBody(
        string DisplayName, string? Visibility, IReadOnlyList<OwnerBody> Owners,
        string? ProviderKey, bool AutoRenew, IReadOnlyList<Guid>? Users, string? Note,
        IReadOnlyList<PhaseInput>? Phases = null, IReadOnlyList<PurchaseInput>? Purchases = null)
    {
        internal CreateSubscriptionCommand? ToCommand(FieldReader reader)
        {
            var visibility = reader.Visibility(Visibility, "visibility");
            var phases = (Phases ?? []).Select((p, i) => p.Read(reader, $"phases[{i}]")).ToList();
            var purchases = (Purchases ?? []).Select((p, i) => p.Read(reader, $"purchases[{i}]")).ToList();
            return reader.Ok
                ? new CreateSubscriptionCommand(
                    DisplayName, visibility!.Value,
                    (Owners ?? []).Select(o => new OwnerShare(new Core.Contracts.PersonId(o.PersonId), o.ShareBp)).ToList(),
                    ProviderKey, AutoRenew,
                    (Users ?? []).Select(u => new Core.Contracts.PersonId(u)).ToList(),
                    Note, phases.Select(p => p!).ToList(), purchases.Select(p => p!).ToList())
                : null;
        }
    }

    public sealed record EditSubscriptionBody(
        bool? AutoRenew, string? CancelledAt, bool ClearCancelledAt, string? Note, string? ProviderKey = null, bool ClearProviderKey = false)
    {
        public EditSubscriptionCommand ToCommand(ItemId id)
            => ClearCancelledAt && CancelledAt is not null
                ? throw new BadHttpRequestException(
                    "cancelledAt and clearCancelledAt contradict each other: send one or the other.")
                : ClearProviderKey && ProviderKey is not null
                    ? throw new BadHttpRequestException(
                        "providerKey and clearProviderKey contradict each other: send one or the other.")
                    : new(id, AutoRenew, OptionalDate(CancelledAt), ClearCancelledAt, Note, ProviderKey, ClearProviderKey);
    }

    public sealed record ClosePhaseBody(string EndsOn);

    public sealed record PhaseBody(
        string StartsOn, string? EndsOn, MoneyBody Price, DurationBody Every, DurationBody? Covers,
        int? MaxCycles, PayerBody Payer, string? Note)
    {
        public OpenPhaseCommand ToOpenCommand(ItemId id) => new(id, new BillingPhase(
            BillingPhaseId.New(),
            RequireDate(StartsOn),
            OptionalDate(EndsOn),
            Price.ToMoney(), Every.ToDuration(), Covers?.ToDuration(), MaxCycles, Payer.ToPayer()));
    }

    /// <summary>A phase as the creation and the price change read it: every refusal located (1E2a design §2.1).</summary>
    public sealed record PhaseInput(
        string? StartsOn, string? EndsOn, MoneyBody? Price, DurationBody? Every, DurationBody? Covers, int? MaxCycles, PayerBody? Payer)
    {
        internal BillingPhase? Read(FieldReader reader, string path)
        {
            var startsOn = reader.Date(StartsOn, path + ".startsOn");
            var endsOn = reader.Date(EndsOn, path + ".endsOn", required: false);
            var price = reader.Money(Price, path + ".price");
            if (price is { Amount: < 0 })
                reader.Add(path + ".price.amount", "subscriptions.price_negative", "A phase has a price of zero or more.");
            var every = reader.Duration(Every, path + ".every");
            var covers = reader.Duration(Covers, path + ".covers", required: false);
            if (MaxCycles is <= 0)
                reader.Add(path + ".maxCycles", "subscriptions.max_cycles", "A cycle limit is positive.");
            var payer = reader.Payer(Payer, path + ".payer");
            return startsOn is { } s && price is { Amount: >= 0 } p && every is { } e && payer is { } y && MaxCycles is not <= 0 && reader.Ok
                ? new BillingPhase(BillingPhaseId.New(), s, endsOn, p, e, covers, MaxCycles, y)
                : null;
        }
    }

    public sealed record PriceChangeBody(
        string? StartsOn, MoneyBody? Price, DurationBody? Every, DurationBody? Covers, int? MaxCycles, PayerBody? Payer)
    {
        internal ChangePriceCommand? ToCommand(ItemId id, FieldReader reader)
        {
            var startsOn = reader.Date(StartsOn, "startsOn");
            var price = reader.Money(Price, "price");
            if (price is { Amount: < 0 })
                reader.Add("price.amount", "subscriptions.price_negative", "A phase has a price of zero or more.");
            var every = reader.Duration(Every, "every", required: false);
            var covers = reader.Duration(Covers, "covers", required: false);
            if (MaxCycles is <= 0)
                reader.Add("maxCycles", "subscriptions.max_cycles", "A cycle limit is positive.");
            var payer = Payer is null ? null : reader.Payer(Payer, "payer");
            return reader.Ok ? new ChangePriceCommand(id, startsOn!.Value, price!.Value, every, covers, MaxCycles, payer) : null;
        }
    }

    public sealed record GrantInput(Guid? SubscriptionId, DurationBody? Duration, string? Source, string? StartsOn);

    /// <summary>A purchase as the creation and the purchase route read it: every refusal located (1E2b1 design §3).</summary>
    public sealed record PurchaseInput(
        string? Date, string? Label, MoneyBody? Price, PayerBody? Payer, string? Kind,
        IReadOnlyList<GrantInput>? Grants, string? AllocatedAmount, string? Note)
    {
        private static readonly Dictionary<string, PurchaseKind> Kinds = new(StringComparer.Ordinal)
        {
            ["prepaid_term"] = PurchaseKind.PrepaidTerm, ["bundle"] = PurchaseKind.Bundle, ["gift"] = PurchaseKind.Gift,
            ["adjustment"] = PurchaseKind.Adjustment, ["refund"] = PurchaseKind.Refund,
        };
        private static readonly Dictionary<string, CoverageSource> Sources = new(StringComparer.Ordinal)
        {
            ["paid"] = CoverageSource.Paid, ["bonus"] = CoverageSource.Bonus, ["included"] = CoverageSource.Included,
            ["trial"] = CoverageSource.Trial, ["gift"] = CoverageSource.Gift,
        };

        private static string At(string prefix, string field) => prefix == "" ? field : $"{prefix}.{field}";

        internal PurchaseDraft? Read(FieldReader reader, string prefix)
        {
            var date = reader.Date(Date, At(prefix, "date"));
            var label = reader.Text(Label, At(prefix, "label"));
            var price = reader.Money(Price, At(prefix, "price"));
            var payer = reader.Payer(Payer, At(prefix, "payer"));
            PurchaseKind? kind = Kind is not null && Kinds.TryGetValue(Kind, out var k) ? k : null;
            if (kind is null)
                reader.Add(At(prefix, "kind"), "request.kind", "A purchase kind is prepaid_term, bundle, gift, adjustment or refund.");
            var allocated = reader.Decimal(AllocatedAmount, At(prefix, "allocatedAmount"));

            var grants = new List<GrantDraft>();
            var list = Grants ?? [];
            for (var j = 0; j < list.Count; j++)
            {
                var path = At(prefix, $"grants[{j}]");
                var duration = reader.Duration(list[j].Duration, path + ".duration");
                CoverageSource? source = list[j].Source is { } s && Sources.TryGetValue(s, out var found) ? found : null;
                if (source is null)
                    reader.Add(path + ".source", "request.source", "A grant source is paid, bonus, included, trial or gift.");
                var startsOn = reader.Date(list[j].StartsOn, path + ".startsOn", required: false);
                if (duration is { } d && source is { } src)
                    grants.Add(new GrantDraft(list[j].SubscriptionId is { } id ? new ItemId(id) : null, d, src, startsOn));
            }

            return reader.Ok
                ? new PurchaseDraft(date!.Value, label!, price!.Value, payer!, kind!.Value, grants, allocated, Note)
                : null;
        }
    }
}
