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
        var api = routes.MapGroup("/api/v1/subscriptions").RequireAuthorization().AddEndpointFilter<ActorFilter>();

        api.MapGet("", async (string? scope, string? from, string? to, string? horizon, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(
                    new ListSubscriptionsQuery(ParseScope(scope), ParseDate(from), ParseDate(to), ParseDate(horizon)), ct))
                .ToHttp(list => list));

        api.MapGet("/{id:guid}", async (Guid id, string? from, string? to, string? horizon, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(
                    new GetSubscriptionQuery(new ItemId(id), ParseDate(from), ParseDate(to), ParseDate(horizon)), ct))
                .ToHttp(detail => detail));

        // Create / open phase / add purchase were implemented early because Task 4's own acceptance test
        // creates its fixtures through the real HTTP API — see the comment atop WriteUseCases.cs.
        api.MapPost("", async (CreateSubscriptionBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(body.ToCommand(), ct)).ToHttp(id => new IdResponse(id.Value), StatusCodes.Status201Created));

        api.MapPatch("/{id:guid}", async (Guid id, EditSubscriptionBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(body.ToCommand(new ItemId(id)), ct)).ToHttp(x => new IdResponse(x.Value)));

        api.MapPost("/{id:guid}/phases", async (Guid id, PhaseBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(body.ToOpenCommand(new ItemId(id)), ct))
                .ToHttp(phase => new IdResponse(phase.Value), StatusCodes.Status201Created));

        api.MapPatch("/{id:guid}/phases/{phaseId:guid}", async (Guid id, Guid phaseId, ClosePhaseBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(
                    new ClosePhaseCommand(new ItemId(id), new BillingPhaseId(phaseId), SubscriptionEndpoints.RequireDate(body.EndsOn)), ct))
                .ToHttp(phase => new IdResponse(phase.Value)));

        api.MapPost("/{id:guid}/purchases", async (Guid id, PurchaseBody body, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(body.ToCommand(new ItemId(id)), ct))
                .ToHttp(purchase => new IdResponse(purchase.Value), StatusCodes.Status201Created));

        api.MapDelete("/{id:guid}/purchases/{purchaseId:guid}", async (Guid id, Guid purchaseId, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new DeletePurchaseCommand(new ItemId(id), new PurchaseId(purchaseId)), ct))
                .ToHttp(purchase => new IdResponse(purchase.Value)));

        return routes;
    }

    /// <summary>Household is the default, matching the header selector of frontend/screens.md §13.</summary>
    internal static VisibilityScope ParseScope(string? scope)
        => string.Equals(scope, "me", StringComparison.OrdinalIgnoreCase) ? VisibilityScope.Me : VisibilityScope.Household;

    internal static LocalDate? ParseDate(string? value)
        => value is null ? null : LocalDatePattern.Iso.Parse(value) is { Success: true } parsed ? parsed.Value : null;

    internal static LocalDate RequireDate(string value)
        => ParseDate(value) ?? throw new BadHttpRequestException($"'{value}' is not an ISO date (yyyy-mm-dd).");

    public sealed record IdResponse(Guid Id);

    public sealed record MoneyBody(decimal Amount, string Currency)
    {
        public Money ToMoney()
            => Core.Contracts.Currency.TryCreate(Currency, out var currency) && Money.Create(Amount, currency).TryGetValue(out var money)
                ? money
                : throw new BadHttpRequestException("An amount and a valid ISO 4217 currency are required.");
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
            => new(id, AutoRenew, SubscriptionEndpoints.ParseDate(CancelledAt), ClearCancelledAt, Note);
    }

    public sealed record ClosePhaseBody(string EndsOn);

    public sealed record PhaseBody(
        string StartsOn, string? EndsOn, MoneyBody Price, DurationBody Every, DurationBody? Covers,
        int? MaxCycles, PayerBody Payer, string? Note)
    {
        public OpenPhaseCommand ToOpenCommand(ItemId id) => new(id, new BillingPhase(
            BillingPhaseId.New(),
            RequireDate(StartsOn),
            EndsOn is null ? null : RequireDate(EndsOn),
            Price.ToMoney(), Every.ToDuration(), Covers?.ToDuration(), MaxCycles, Payer.ToPayer()));
    }

    public sealed record GrantBody(Guid SubscriptionId, DurationBody Duration, string Source, string? StartsOn)
    {
        public Grant ToGrant() => new(
            new ItemId(SubscriptionId), Duration.ToDuration(),
            Enum.TryParse<CoverageSource>(Source, ignoreCase: true, out var source)
                ? source
                : throw new BadHttpRequestException("A grant source of paid, bonus, included, trial or gift is required."),
            StartsOn is null ? null : RequireDate(StartsOn));
    }

    public sealed record PurchaseBody(
        string Date, string Label, MoneyBody Price, PayerBody Payer, string Kind,
        IReadOnlyList<GrantBody> Grants, decimal? AllocatedAmount, string? Note)
    {
        public AddPurchaseCommand ToCommand(ItemId id) => new(id, new Purchase(
            PurchaseId.New(),
            RequireDate(Date), Label, Price.ToMoney(), Payer.ToPayer(),
            Kind == "prepaid_term" ? PurchaseKind.PrepaidTerm
                : Enum.TryParse<PurchaseKind>(Kind, ignoreCase: true, out var kind) ? kind
                : throw new BadHttpRequestException("A purchase kind of prepaid_term, bundle, gift, adjustment or refund is required."),
            Grants.Select(g => g.ToGrant()).ToList(),
            AllocatedAmount));
    }
}
