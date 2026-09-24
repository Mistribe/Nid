using System.Globalization;
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
        api.MapPost("", async (CreateSubscriptionBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(body.ToCommand(), ct)).ToHttp(id => new IdResponse(id.Value), StatusCodes.Status201Created))
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

        api.MapPost("/{id:guid}/purchases", async (Guid id, PurchaseBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(body.ToCommand(new ItemId(id)), ct))
                .ToHttp(purchase => new IdResponse(purchase.Value), StatusCodes.Status201Created))
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
        string DisplayName, string Visibility, IReadOnlyList<OwnerBody> Owners,
        string? ProviderKey, bool AutoRenew, IReadOnlyList<Guid>? Users, string? Note)
    {
        public CreateSubscriptionCommand ToCommand() => new(
            DisplayName,
            Enum.TryParse<Visibility>(Visibility, ignoreCase: true, out var visibility)
                ? visibility
                : throw new BadHttpRequestException("Visibility is shared or private."),
            Owners.Select(o => new OwnerShare(new Core.Contracts.PersonId(o.PersonId), o.ShareBp)).ToList(),
            ProviderKey, AutoRenew,
            (Users ?? []).Select(u => new Core.Contracts.PersonId(u)).ToList(),
            Note);
    }

    public sealed record EditSubscriptionBody(bool? AutoRenew, string? CancelledAt, bool ClearCancelledAt, string? Note)
    {
        public EditSubscriptionCommand ToCommand(ItemId id)
            => ClearCancelledAt && CancelledAt is not null
                ? throw new BadHttpRequestException(
                    "cancelledAt and clearCancelledAt contradict each other: send one or the other.")
                : new(id, AutoRenew, OptionalDate(CancelledAt), ClearCancelledAt, Note);
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

    public sealed record GrantBody(Guid SubscriptionId, DurationBody Duration, string Source, string? StartsOn)
    {
        public Grant ToGrant() => new(
            new ItemId(SubscriptionId), Duration.ToDuration(),
            Enum.TryParse<CoverageSource>(Source, ignoreCase: true, out var source)
                ? source
                : throw new BadHttpRequestException("A grant source of paid, bonus, included, trial or gift is required."),
            OptionalDate(StartsOn));
    }

    public sealed record PurchaseBody(
        string Date, string Label, MoneyBody Price, PayerBody Payer, string Kind,
        IReadOnlyList<GrantBody> Grants, string? AllocatedAmount, string? Note)
    {
        public AddPurchaseCommand ToCommand(ItemId id) => new(id, new Purchase(
            PurchaseId.New(),
            RequireDate(Date), Label, Price.ToMoney(), Payer.ToPayer(),
            Kind == "prepaid_term" ? PurchaseKind.PrepaidTerm
                : Enum.TryParse<PurchaseKind>(Kind, ignoreCase: true, out var kind) ? kind
                : throw new BadHttpRequestException("A purchase kind of prepaid_term, bundle, gift, adjustment or refund is required."),
            Grants.Select(g => g.ToGrant()).ToList(),
            OptionalAmount(AllocatedAmount)));
    }

    private static decimal? OptionalAmount(string? value)
        => value is null ? null
            : decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
                ? amount
                : throw new BadHttpRequestException($"'{value}' is not a decimal string.");
}
