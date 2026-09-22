using System.Reflection;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Application;
using Nid.Subscriptions.Contracts;
using NodaTime;
using UnambitiousFx.Synapse.Abstractions;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Subscriptions.IntegrationTests.LeakBench;

// .Ok() is the test project's ResultAssertions helper (Task 3), not the store's private unwrapper.

/// <summary>How the bench builds one instance of a request, for a given item of the dataset.</summary>
public sealed record CatalogueEntry(Type RequestType, bool IsItemScoped, Func<MixedDataset, ItemId, object> Build);

/// <summary>
/// Every request the bench knows how to run. Discovery is automatic, so a new request is found without
/// anyone remembering to list it here — and fails the bench until someone adds it (access-tests.md §6).
/// </summary>
public static class RequestCatalogue
{
    public static IReadOnlyList<Assembly> ModuleAssemblies =>
    [
        typeof(ListPersonsQuery).Assembly,           // Nid.Core.Application
        typeof(ListSubscriptionsQuery).Assembly,     // Nid.Subscriptions.Application
    ];

    /// <summary>
    /// The <c>RegisterGroup</c>s Synapse generates, composed exactly as <c>NidServiceCollectionExtensions</c>
    /// composes them. Replaying one against <see cref="RecordingBuilder"/> is what access-tests.md §3.1 asks
    /// for: the requests the application actually registers, not a list maintained by hand.
    /// </summary>
    public static IReadOnlyList<IRegisterGroup> RegisterGroups =>
    [
        new Nid.Host.RegisterGroup(),                  // the composition root: carries the global behaviors
        new Nid.Core.Application.RegisterGroup(),
        new Nid.Subscriptions.Application.RegisterGroup(),
    ];

    /// <summary>Every concrete request type declared in the module assemblies.</summary>
    public static IReadOnlyList<Type> Declared
        => ModuleAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false } && IsRequest(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

    /// <summary>Every request type Synapse is told to handle, read back from the generated register groups.</summary>
    public static IReadOnlyList<Type> Registered
    {
        get
        {
            var recorder = new RecordingBuilder();
            foreach (var group in RegisterGroups)
                group.Register(recorder);
            return recorder.Requests.OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// What the bench must cover. The union of both discoveries on purpose: a request that is declared but
    /// never registered is unreachable and still has to be explained, and a request registered from an
    /// assembly the bench does not scan must not slip through either.
    /// </summary>
    public static IReadOnlyList<Type> Discover()
        => Declared.Union(Registered).OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();

    public static IReadOnlyList<CatalogueEntry> Entries { get; } =
    [
        new(typeof(ListPersonsQuery), false, (_, _) => new ListPersonsQuery()),
        new(typeof(CreateDependentCommand), false, (_, _) => new CreateDependentCommand("bench-dependent")),
        new(typeof(ProvisionAdultCommand), false, (_, _) => new ProvisionAdultCommand("bench-subject", "bench", null)),

        new(typeof(ListSubscriptionsQuery), false,
            (_, _) => new ListSubscriptionsQuery(VisibilityScope.Me, null, null, null)),
        new(typeof(GetSubscriptionQuery), true,
            (_, item) => new GetSubscriptionQuery(item, null, null, null)),
        new(typeof(CreateSubscriptionCommand), false,
            (data, _) => new CreateSubscriptionCommand(
                "bench-created", Visibility.Shared, [new OwnerShare(data.App.AdultA, 10_000)], null, true, [], null)),
        new(typeof(EditSubscriptionCommand), true,
            (_, item) => new EditSubscriptionCommand(item, AutoRenew: false, null, false, null)),
        new(typeof(OpenPhaseCommand), true,
            (_, item) => new OpenPhaseCommand(item, new BillingPhase(
                BillingPhaseId.New(), new LocalDate(2025, 1, 1), null,
                Money.Create(1m, Currency.Eur).Ok(), Duration.Create(1, DurationUnit.Month).Ok(),
                null, null, new Payer(PayerType.Household, null)))),
        new(typeof(ClosePhaseCommand), true,
            (_, item) => new ClosePhaseCommand(item, BillingPhaseId.New(), new LocalDate(2025, 6, 1))),
        new(typeof(AddPurchaseCommand), true,
            (_, item) => new AddPurchaseCommand(item, new Purchase(
                PurchaseId.New(), new LocalDate(2025, 1, 1), "bench", Money.Create(1m, Currency.Eur).Ok(),
                new Payer(PayerType.Household, null), PurchaseKind.PrepaidTerm,
                [new Grant(item, Duration.Create(1, DurationUnit.Month).Ok(), CoverageSource.Paid, null)], null))),
        new(typeof(DeletePurchaseCommand), true,
            (_, item) => new DeletePurchaseCommand(item, PurchaseId.New())),
    ];

    public static IReadOnlySet<Type> Covered => Entries.Select(e => e.RequestType).ToHashSet();

    /// <summary>The response type of a request, i.e. the <c>T</c> of its <c>IRequest&lt;T&gt;</c>.</summary>
    public static Type? ResponseTypeOf(Type request)
        => request.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>))
            ?.GetGenericArguments()[0];

    private static bool IsRequest(Type type)
        => typeof(IRequest).IsAssignableFrom(type)
           || type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>));

    /// <summary>
    /// A no-op <see cref="IDependencyInjectionBuilder"/> that only remembers which request types were
    /// registered. Synapse's own registry (<c>InvokerOptions.RequestDispatchers</c>) is internal, but the
    /// register groups that fill it are public and replaying one here costs nothing.
    /// </summary>
    private sealed class RecordingBuilder : IDependencyInjectionBuilder
    {
        public HashSet<Type> Requests { get; } = [];

        public IDependencyInjectionBuilder RegisterRequestHandler<TRequestHandler, TRequest, TResponse>()
            where TRequestHandler : class, IRequestHandler<TRequest, TResponse>
            where TRequest : IRequest<TResponse>
            where TResponse : notnull
            => Record(typeof(TRequest));

        public IDependencyInjectionBuilder RegisterRequestHandler<TRequestHandler, TRequest>()
            where TRequestHandler : class, IRequestHandler<TRequest>
            where TRequest : IRequest
            => Record(typeof(TRequest));

        public IDependencyInjectionBuilder RegisterRequestHandlerWhen<TRequestHandler, TRequest, TResponse>(Func<bool> condition)
            where TRequestHandler : class, IRequestHandler<TRequest, TResponse>
            where TRequest : IRequest<TResponse>
            where TResponse : notnull
            => Record(typeof(TRequest));

        public IDependencyInjectionBuilder RegisterRequestHandlerWhen<TRequestHandler, TRequest>(Func<bool> condition)
            where TRequestHandler : class, IRequestHandler<TRequest>
            where TRequest : IRequest
            => Record(typeof(TRequest));

        public IDependencyInjectionBuilder RegisterStreamRequestHandler<TStreamRequestHandler, TRequest, TItem>()
            where TStreamRequestHandler : class, IStreamRequestHandler<TRequest, TItem>
            where TRequest : IStreamRequest<TItem>
            where TItem : notnull
            => Record(typeof(TRequest));

        public IDependencyInjectionBuilder RegisterStreamRequestHandlerWhen<TStreamRequestHandler, TRequest, TItem>(Func<bool> condition)
            where TStreamRequestHandler : class, IStreamRequestHandler<TRequest, TItem>
            where TRequest : IStreamRequest<TItem>
            where TItem : notnull
            => Record(typeof(TRequest));

        public IDependencyInjectionBuilder RegisterEventHandler<TEventHandler, TEvent>()
            where TEventHandler : class, IEventHandler<TEvent>
            where TEvent : class, IEvent
            => this;

        public IDependencyInjectionBuilder RegisterEventHandlerWhen<TEventHandler, TEvent>(Func<bool> condition)
            where TEventHandler : class, IEventHandler<TEvent>
            where TEvent : class, IEvent
            => this;

        public IDependencyInjectionBuilder RegisterRequestPipelineBehavior<TBehavior, TRequest>()
            where TBehavior : class, IRequestPipelineBehavior<TRequest>
            where TRequest : IRequest
            => this;

        public IDependencyInjectionBuilder RegisterRequestPipelineBehavior<TBehavior, TRequest, TResponse>()
            where TBehavior : class, IRequestPipelineBehavior<TRequest, TResponse>
            where TRequest : IRequest<TResponse>
            where TResponse : notnull
            => this;

        public IDependencyInjectionBuilder RegisterStreamRequestPipelineBehavior<TBehavior, TRequest, TItem>()
            where TBehavior : class, IStreamRequestPipelineBehavior<TRequest, TItem>
            where TRequest : IStreamRequest<TItem>
            where TItem : notnull
            => this;

        public IDependencyInjectionBuilder RegisterEventPipelineBehavior<TBehavior, TEvent>()
            where TBehavior : class, IEventPipelineBehavior<TEvent>
            where TEvent : class, IEvent
            => this;

        public IDependencyInjectionBuilder RegisterCqrsBoundaryEnforcement<TRequest>()
            where TRequest : IRequest
            => this;

        public IDependencyInjectionBuilder RegisterCqrsBoundaryEnforcement<TRequest, TResponse>()
            where TRequest : IRequest<TResponse>
            where TResponse : notnull
            => this;

        public IDependencyInjectionBuilder RegisterValidator<TValidator, TRequest>()
            where TValidator : class, IRequestValidator<TRequest>
            where TRequest : IRequest
            => this;

        public IDependencyInjectionBuilder RegisterValidator<TValidator, TRequest, TResponse>()
            where TValidator : class, IRequestValidator<TRequest>
            where TRequest : IRequest<TResponse>
            where TResponse : notnull
            => this;

        private IDependencyInjectionBuilder Record(Type request)
        {
            Requests.Add(request);
            return this;
        }
    }
}
