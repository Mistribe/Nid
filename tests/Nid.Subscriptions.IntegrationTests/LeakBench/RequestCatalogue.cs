using System.Reflection;
using System.Text.Json;
using Nid.Core.Application;
using Nid.Core.Application.Admin;
using Nid.Core.Application.Fx;
using Nid.Core.Contracts;
using Nid.Investments.Application;
using Nid.Investments.Contracts;
using Nid.Platform;
using Nid.Platform.Admin;
using Nid.Platform.Audit;
using Nid.Platform.Contracts;
using Nid.Platform.Export;
using Nid.Platform.Import;
using Nid.Subscriptions.Application;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using NodaTime;
using UnambitiousFx.Synapse.Abstractions;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Subscriptions.IntegrationTests.LeakBench;

// .Ok() is the test project's ResultAssertions helper (Task 3), not the store's private unwrapper.

/// <summary>How the bench builds one instance of a request, for a given item of the dataset.</summary>
public sealed record CatalogueEntry(Type RequestType, bool IsItemScoped, Func<MixedDataset, ItemId, object> Build)
{
    /// <summary>
    /// The bench's second axis: the same request aimed at <c>target</c> while naming a second, different
    /// item. <see cref="Build"/> alone only ever names one item, which is precisely why the bench reported
    /// full coverage while a purchase could grant to — and a delete cascade into — an item the actor may not
    /// write. Required of every item-scoped request whose payload can reach another <see cref="ItemId"/>,
    /// which <c>LeakBenchTests</c> checks by reflection; <c>null</c> when the request cannot name one.
    /// Asynchronous because a request may name the second item indirectly, through a row that has to exist
    /// first — a purchase id, for instance.
    /// </summary>
    public Func<MixedDataset, ItemId, ItemId, Task<object>>? BuildNamingAnother { get; init; }

    /// <summary>
    /// The provider axis: the request, as <c>actor</c> (a creation must name its creator among the owners), aimed at
    /// <c>target</c> while naming a provider only <em>another</em> item uses. A provider is visible through the
    /// subscriptions that use it (design §3.5), so linking one is a way to reach a second item; required of every
    /// request carrying a <c>ProviderKey</c>, which <c>LeakBenchTests</c> checks by reflection.
    /// </summary>
    public Func<MixedDataset, PersonId, ItemId, string, object>? BuildLinkingProvider { get; init; }
}

/// <summary>
/// Every request the bench knows how to run. Discovery is automatic, so a new request is found without
/// anyone remembering to list it here — and fails the bench until someone adds it (access-tests.md §6).
/// </summary>
public static class RequestCatalogue
{
    /// <summary>
    /// Every <c>Nid.*</c> assembly the composition root can reach. Derived, never transcribed: a module that
    /// 1D adds and wires into <c>AddNid</c> becomes visible to the bench because <c>Nid.Host</c> must
    /// reference it to call it — whereas a hand-written list would silently leave it unexamined while the
    /// bench reported full coverage.
    /// </summary>
    public static IReadOnlyList<Assembly> ModuleAssemblies { get; } = CompositionRootClosure();

    /// <summary>
    /// The <c>RegisterGroup</c>s Synapse generates, found in that same closure rather than listed by hand.
    /// Replaying one against <see cref="RecordingBuilder"/> is what access-tests.md §3.1 asks for: the
    /// requests the application actually registers.
    /// </summary>
    public static IReadOnlyList<IRegisterGroup> RegisterGroups { get; } = ModuleAssemblies
        .SelectMany(a => a.GetTypes())
        .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false }
                    && typeof(IRegisterGroup).IsAssignableFrom(t)
                    && t.GetConstructor(Type.EmptyTypes) is not null)
        .OrderBy(t => t.FullName, StringComparer.Ordinal)
        .Select(t => (IRegisterGroup)Activator.CreateInstance(t)!)
        .ToList();

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
        new(typeof(UpdatePreferencesCommand), false,
            (_, _) => new UpdatePreferencesCommand(new PreferencesChange("sauge", null, null))),
        new(typeof(GetCapabilitiesQuery), false, (_, _) => new GetCapabilitiesQuery()),

        new(typeof(CollectDailyFxRatesCommand), false, (_, _) => new CollectDailyFxRatesCommand()),
        new(typeof(BackfillFxRatesCommand), false, (_, _) => new BackfillFxRatesCommand()),
        new(typeof(RequestFxSyncCommand), false, (_, _) => new RequestFxSyncCommand()),
        new(typeof(SetManualFxRateCommand), false,
            (_, _) => new SetManualFxRateCommand(new NodaTime.LocalDate(2025, 1, 2), "EUR", "0.95")),
        new(typeof(GetFxRatesQuery), false,
            (_, _) => new GetFxRatesQuery("EUR", new NodaTime.LocalDate(2025, 1, 1), new NodaTime.LocalDate(2025, 1, 31))),
        new(typeof(GetFxSyncRunsQuery), false, (_, _) => new GetFxSyncRunsQuery()),
        new(typeof(SyncCatalogCommand), false, (_, _) => new SyncCatalogCommand()),
        new(typeof(RequestCatalogSyncCommand), false, (_, _) => new RequestCatalogSyncCommand()),
        new(typeof(GetCatalogRunsQuery), false, (_, _) => new GetCatalogRunsQuery()),
        new(typeof(ListProvidersQuery), false, (_, _) => new ListProvidersQuery(null)),
        new(typeof(ListLabelsQuery), false, (_, _) => new ListLabelsQuery()),
        new(typeof(CreateProviderCommand), false,
            (_, _) => new CreateProviderCommand("bench-provider", null, null, [CatalogLimits.OtherLabel])),
        new(typeof(GetProviderIconQuery), false, (_, _) => new GetProviderIconQuery(new string('0', 64))),

        new(typeof(ListAdultsQuery), false, (_, _) => new ListAdultsQuery()),
        new(typeof(PreRegisterAdultCommand), false, (_, _) => new PreRegisterAdultCommand("bench-adult", "bench@example.test")),
        new(typeof(DeletePendingAdultCommand), false, (_, _) => new DeletePendingAdultCommand(PersonId.New())),
        new(typeof(SetAdminCommand), false, (data, _) => new SetAdminCommand(data.App.AdultB, false)),
        new(typeof(RevokeSessionsCommand), false, (data, _) => new RevokeSessionsCommand(data.App.AdultB)),
        new(typeof(ListAllPersonsQuery), false, (_, _) => new ListAllPersonsQuery()),
        new(typeof(RebindSubjectCommand), false, (data, _) => new RebindSubjectCommand(data.App.AdultB, "bench-sub")),
        new(typeof(RevokeMySessionsCommand), false, (_, _) => new RevokeMySessionsCommand()),
        new(typeof(GetHouseholdSettingsQuery), false, (_, _) => new GetHouseholdSettingsQuery()),
        new(typeof(UpdateHouseholdSettingsCommand), false, (_, _) => new UpdateHouseholdSettingsCommand(null, null)),
        new(typeof(ListJobsQuery), false, (_, _) => new ListJobsQuery()),
        new(typeof(GetSystemStatusQuery), false, (_, _) => new GetSystemStatusQuery()),
        new(typeof(ItemHistoryQuery), true, (_, item) => new ItemHistoryQuery(item, null, null)),
        new(typeof(MySecurityEventsQuery), false, (_, _) => new MySecurityEventsQuery(null, null)),
        new(typeof(SecurityJournalQuery), false, (_, _) => new SecurityJournalQuery(null, null, null)),

        new(typeof(ListSubscriptionsQuery), false,
            (_, _) => new ListSubscriptionsQuery(VisibilityScope.Me, null, null, null)),
        new(typeof(GetSubscriptionQuery), true,
            (_, item) => new GetSubscriptionQuery(item, null, null, null)),
        new(typeof(CreateSubscriptionCommand), false,
            (data, _) => new CreateSubscriptionCommand(
                "bench-created", Visibility.Shared, [new OwnerShare(data.App.AdultA, 10_000)], null, true, [], null))
        {
            BuildLinkingProvider = (_, actor, _, key) => new CreateSubscriptionCommand(
                "bench-linked", Visibility.Shared, [new OwnerShare(actor, 10_000)], key, true, [], null),
        },
        new(typeof(EditSubscriptionCommand), true,
            (_, item) => new EditSubscriptionCommand(item, AutoRenew: false, null, false, null))
        {
            BuildLinkingProvider = (_, _, target, key) => new EditSubscriptionCommand(target, null, null, false, null, key),
        },
        new(typeof(ChangePriceCommand), true,
            (_, item) => new ChangePriceCommand(item, new NodaTime.LocalDate(2025, 6, 1), Money.Create(1m, Currency.Eur).Ok(), null, null, null, null)),
        new(typeof(OpenPhaseCommand), true,
            (_, item) => new OpenPhaseCommand(item, new BillingPhase(
                BillingPhaseId.New(), new LocalDate(2025, 1, 1), null,
                Money.Create(1m, Currency.Eur).Ok(), Duration.Create(1, DurationUnit.Month).Ok(),
                null, null, new Payer(PayerType.Household, null)))),
        new(typeof(ClosePhaseCommand), true,
            (_, item) => new ClosePhaseCommand(item, BillingPhaseId.New(), new LocalDate(2025, 6, 1))),
        new(typeof(AddPurchaseCommand), true,
            (_, item) => new AddPurchaseCommand(item, new PurchaseDraft(
                new LocalDate(2025, 1, 1), "bench", Money.Create(1m, Currency.Eur).Ok(),
                new Payer(PayerType.Household, null), PurchaseKind.PrepaidTerm,
                [new GrantDraft(null, Duration.Create(1, DurationUnit.Month).Ok(), CoverageSource.Paid, null)], null, null)))
        {
            // Grants are what makes one purchase touch two subscriptions: the route's, and any other.
            BuildNamingAnother = (_, target, other) => Task.FromResult<object>(new AddPurchaseCommand(target, new PurchaseDraft(
                new LocalDate(2025, 1, 1), "bench-two", Money.Create(1m, Currency.Eur).Ok(),
                new Payer(PayerType.Household, null), PurchaseKind.PrepaidTerm,
                [
                    new GrantDraft(null, Duration.Create(1, DurationUnit.Month).Ok(), CoverageSource.Paid, null),
                    new GrantDraft(other, Duration.Create(1, DurationUnit.Month).Ok(), CoverageSource.Paid, null),
                ], null, null))),
        },
        new(typeof(DeletePurchaseCommand), true,
            (_, item) => new DeletePurchaseCommand(item, PurchaseId.New()))
        {
            // A delete names its second item indirectly: the purchase carries grants to both, and removing
            // it cascades into both. The row has to exist before the request can point at it.
            BuildNamingAnother = async (data, target, other) =>
                new DeletePurchaseCommand(target, await data.App.SeedPurchaseGrantingToAsync(target, other)),
        },

        // Import sessions are the person's own, not items: B naming A's session id is the same 404 as a
        // random id, which ImportTests checks endpoint by endpoint (SessionTests, BatchTests).
        new(typeof(StartImportCommand), false,
            (_, _) => new StartImportCommand("nid.data", 1, new Dictionary<string, PersonId>())),
        new(typeof(GetImportQuery), false, (_, _) => new GetImportQuery(Guid.NewGuid())),
        new(typeof(ListImportsQuery), false, (_, _) => new ListImportsQuery()),
        new(typeof(CancelImportCommand), false, (_, _) => new CancelImportCommand(Guid.NewGuid())),
        new(typeof(PutBatchCommand), false,
            (_, _) => new PutBatchCommand(Guid.NewGuid(), "persons", 0, new string('0', 64), JsonDocument.Parse("[]").RootElement)),
        new(typeof(ValidateImportCommand), false, (_, _) => new ValidateImportCommand(Guid.NewGuid())),
        new(typeof(CommitImportCommand), false, (_, _) => new CommitImportCommand(Guid.NewGuid())),

        // The export is the reader's own view of what they can read; its views expose every exported item to the
        // output guard (1D2 design §6), and ExportPrivacyTests checks the content.
        new(typeof(ExportManifestQuery), false, (_, _) => new ExportManifestQuery()),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("subscriptions", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("envelopes", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("valuations", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("flows", null, null)),

        new(typeof(ListEnvelopesQuery), false, (_, _) => new ListEnvelopesQuery(VisibilityScope.Me, true)),
        new(typeof(GetEnvelopeQuery), true, (_, item) => new GetEnvelopeQuery(item)),
        new(typeof(EditEnvelopeCommand), true, (_, item) => new EditEnvelopeCommand(item, null, null, new EnvelopeChange(Note: "bench"), null)),
        new(typeof(CreateEnvelopeCommand), false, (d, _) => new CreateEnvelopeCommand("bench", "savings", Currency.Eur, Visibility.Shared,
            [new OwnerShare(d.App.AdultA, 10_000)], null, null, null, null, null, null, null, null)),

        // A fact id is looked up by (id, item_id) in the Core (2A), so a fact id alone can never reach another
        // item: no BuildNamingAnother (CarriesItemIds only matches ItemId itself; a Guid fact id does not).
        new(typeof(RecordValuationCommand), true, (_, item) => new RecordValuationCommand(item, new LocalDate(2025, 1, 1), Money.Create(1m, Currency.Eur).Ok(), null)),
        new(typeof(VoidValuationCommand), true, (_, item) => new VoidValuationCommand(item, Guid.NewGuid())),
        new(typeof(RecordFlowCommand), true, (_, item) => new RecordFlowCommand(item, new FlowDraft(new LocalDate(2025, 1, 1), "contribution", Money.Create(1m, Currency.Eur).Ok(), null, null, null))),
        new(typeof(CorrectFlowCommand), true, (_, item) => new CorrectFlowCommand(item, Guid.NewGuid(), new FlowDraft(new LocalDate(2025, 1, 1), "contribution", Money.Create(1m, Currency.Eur).Ok(), null, null, null))),
        new(typeof(VoidFlowCommand), true, (_, item) => new VoidFlowCommand(item, Guid.NewGuid())),

        new(typeof(TransferCommand), true, (_, item) => new TransferCommand(item, item, new LocalDate(2025, 1, 1), Money.Create(1m, Currency.Eur).Ok(), null))
        {
            // A transfer names two envelopes: the route's source, and its destination. The base Build names
            // the source twice — it is refused on its own merits (investments.transfer_same_envelope) before
            // the destination's access is even checked, so it never manufactures a bogus item-not-found.
            BuildNamingAnother = (_, target, other) => Task.FromResult<object>(
                new TransferCommand(target, other, new LocalDate(2025, 1, 1), Money.Create(1m, Currency.Eur).Ok(), null)),
        },
        new(typeof(CloseEnvelopeCommand), true, (_, item) => new CloseEnvelopeCommand(item, new LocalDate(2025, 1, 1), null)),
        new(typeof(ReopenEnvelopeCommand), true, (_, item) => new ReopenEnvelopeCommand(item)),
        new(typeof(GetEnvelopeSeriesQuery), true, (_, item) => new GetEnvelopeSeriesQuery(item)),
        new(typeof(GetPeriodPerformanceQuery), true, (_, item) => new GetPeriodPerformanceQuery(item, new LocalDate(2025, 1, 1), new LocalDate(2025, 6, 30))),
    ];

    public static IReadOnlySet<Type> Covered => Entries.Select(e => e.RequestType).ToHashSet();

    /// <summary>The response type of a request, i.e. the <c>T</c> of its <c>IRequest&lt;T&gt;</c>.</summary>
    public static Type? ResponseTypeOf(Type request)
        => request.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>))
            ?.GetGenericArguments()[0];

    /// <summary>
    /// <c>Nid.Host</c> — where <c>AddNid</c> lives — and every <c>Nid.*</c> assembly it references, directly
    /// or not. Anything <c>AddNid</c> can wire up is in here by construction.
    /// </summary>
    private static IReadOnlyList<Assembly> CompositionRootClosure()
    {
        var found = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var pending = new Queue<Assembly>([typeof(Nid.Host.NidServiceCollectionExtensions).Assembly]);

        while (pending.Count > 0)
        {
            var assembly = pending.Dequeue();
            if (!found.TryAdd(assembly.GetName().Name!, assembly))
                continue;

            foreach (var reference in assembly.GetReferencedAssemblies())
                if (reference.Name?.StartsWith("Nid.", StringComparison.Ordinal) == true)
                    pending.Enqueue(Assembly.Load(reference));
        }

        return found.Values.OrderBy(a => a.GetName().Name, StringComparer.Ordinal).ToList();
    }

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
