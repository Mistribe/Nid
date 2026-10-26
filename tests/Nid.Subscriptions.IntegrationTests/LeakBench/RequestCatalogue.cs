using Nid.Core.Application.Tax;
using Nid.Core.Domain.Tax;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application;
using Nid.Core.Application.Admin;
using Nid.Core.Application.Dashboard;
using Nid.Core.Application.Fx;
using Nid.Core.Application.Scenarios;
using Nid.Core.Domain.Scenarios;
using Nid.Core.Contracts;
using Nid.Documents.Application;
using Nid.Documents.Contracts;
using Nid.Goals.Application;
using Nid.Goals.Contracts;
using Nid.Income.Application;
using Nid.Income.Contracts;
using Nid.Income.Domain;
using Nid.Investments.Application;
using Nid.Investments.Application.Prices;
using Nid.RealEstate.Application.Irl;
using Nid.Investments.Contracts;
using Nid.Platform;
using Nid.Platform.Admin;
using Nid.Platform.Audit;
using Nid.Platform.Contracts;
using Nid.Platform.Export;
using Nid.Platform.Import;
using Nid.Platform.Notifications;
using Nid.RealEstate.Application;
using Nid.RealEstate.Contracts;
using Nid.Subscriptions.Application;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.IntegrationTests.Documents;
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

    /// <summary>
    /// The access the second axis expects on the second item: <c>Write</c> for a write reaching two items, <c>Read</c> for a link
    /// to an item the actor only has to read (spec 3C1 §3: a loan's property).
    /// </summary>
    public AccessKind SecondItemAccess { get; init; } = AccessKind.Write;

    /// <summary>
    /// A request that is not item-scoped but is built on a row's own resource (a document): the read scan runs it on every row rather
    /// than on S-A alone, and the bench checks each refusal against the row.
    /// </summary>
    public bool OnEveryRow { get; init; }
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
            (_, _) => new UpdatePreferencesCommand(new PreferencesChange("sauge", null, null, null))),
        new(typeof(GetCapabilitiesQuery), false, (_, _) => new GetCapabilitiesQuery()),

        new(typeof(CollectDailyFxRatesCommand), false, (_, _) => new CollectDailyFxRatesCommand()),
        new(typeof(BackfillFxRatesCommand), false, (_, _) => new BackfillFxRatesCommand()),
        new(typeof(RequestFxSyncCommand), false, (_, _) => new RequestFxSyncCommand()),
        new(typeof(SetManualFxRateCommand), false,
            (_, _) => new SetManualFxRateCommand(new NodaTime.LocalDate(2025, 1, 2), "EUR", "0.95")),
        new(typeof(GetFxRatesQuery), false,
            (_, _) => new GetFxRatesQuery("EUR", new NodaTime.LocalDate(2025, 1, 1), new NodaTime.LocalDate(2025, 1, 31))),
        new(typeof(GetFxSyncRunsQuery), false, (_, _) => new GetFxSyncRunsQuery()),
        new(typeof(CollectPricesCommand), false, (_, _) => new CollectPricesCommand()),
        new(typeof(RequestPriceSyncCommand), false, (_, _) => new RequestPriceSyncCommand()),
        new(typeof(GetPriceRunsQuery), false, (_, _) => new GetPriceRunsQuery()),
        new(typeof(CollectIrlCommand), false, (_, _) => new CollectIrlCommand()),
        new(typeof(RequestIrlSyncCommand), false, (_, _) => new RequestIrlSyncCommand()),
        new(typeof(GetIrlQuery), false, (_, _) => new GetIrlQuery()),
        new(typeof(SetIrlValueCommand), false, (_, _) => new SetIrlValueCommand(2026, 2, 148.37m, new NodaTime.LocalDate(2026, 7, 12))),
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
        new(typeof(RevokeSessionsCommand), false, (data, _) => new RevokeSessionsCommand(data.App.AdultB)),
        new(typeof(ListAllPersonsQuery), false, (_, _) => new ListAllPersonsQuery()),
        new(typeof(RebindSubjectCommand), false, (data, _) => new RebindSubjectCommand(data.App.AdultB, "bench-sub")),
        new(typeof(RevokeMySessionsCommand), false, (_, _) => new RevokeMySessionsCommand()),
        new(typeof(ListMyTokensQuery), false, (_, _) => new ListMyTokensQuery()),
        new(typeof(GetMyPrivateItemsQuery), false, (_, _) => new GetMyPrivateItemsQuery()),
        new(typeof(CreateTokenCommand), false, (_, _) => new CreateTokenCommand("bench-token", TokenPresets.Read, 30)),
        new(typeof(RevokeMyTokenCommand), false, (_, _) => new RevokeMyTokenCommand(Guid.NewGuid())),
        new(typeof(RevokeTokensCommand), false, (data, _) => new RevokeTokensCommand(data.App.AdultB)),
        new(typeof(SubmitProposalCommand), true, (_, item) => new SubmitProposalCommand("nope", item, System.Text.Json.JsonDocument.Parse("{}").RootElement, null)),
        new(typeof(ApplyDirectCommand), true, (_, item) => new ApplyDirectCommand("nope", item, System.Text.Json.JsonDocument.Parse("{}").RootElement)),
        new(typeof(ListProposalsQuery), false, (_, _) => new ListProposalsQuery(null)),
        new(typeof(GetProposalQuery), false, (data, _) => new GetProposalQuery(data.Proposals["P-B"][2])),
        new(typeof(ApproveProposalCommand), false, (data, _) => new ApproveProposalCommand(data.Proposals["P-B"][0], 0, null)),
        new(typeof(RejectProposalCommand), false, (data, _) => new RejectProposalCommand(data.Proposals["P-B"][0], 0, null)),
        new(typeof(GetHouseholdSettingsQuery), false, (_, _) => new GetHouseholdSettingsQuery()),
        new(typeof(UpdateHouseholdSettingsCommand), false, (_, _) => new UpdateHouseholdSettingsCommand(null, null, null)),
        new(typeof(ListJobsQuery), false, (_, _) => new ListJobsQuery()),
        new(typeof(GetSystemStatusQuery), false, (_, _) => new GetSystemStatusQuery()),
        new(typeof(ItemHistoryQuery), true, (_, item) => new ItemHistoryQuery(item, null, null)),
        new(typeof(MySecurityEventsQuery), false, (_, _) => new MySecurityEventsQuery(null, null)),
        new(typeof(SecurityJournalQuery), false, (_, _) => new SecurityJournalQuery(null, null, null)),

        new(typeof(ListSubscriptionsQuery), false,
            (_, _) => new ListSubscriptionsQuery(VisibilityScope.Me, null, null, null)),
        new(typeof(GetSubscriptionsOverviewQuery), false,
            (_, _) => new GetSubscriptionsOverviewQuery(VisibilityScope.Me)),
        new(typeof(GetSubscriptionChargesQuery), false,
            (_, _) => new GetSubscriptionChargesQuery(VisibilityScope.Me, null)),
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

        // Notifications are the person's own rows, filtered by visibility on read (notifications design §4): B naming A's
        // notification id is the same 404 as a random id, which NotificationEndpointTests checks. The evaluation is admin-only
        // and per person; it names no item. The preferences are the person's own row, keyed by the actor.
        new(typeof(ListNotificationsQuery), false, (_, _) => new ListNotificationsQuery(NotificationList.Todo, null, null)),
        new(typeof(CountNotificationsQuery), false, (_, _) => new CountNotificationsQuery()),
        new(typeof(ReadNotificationCommand), false, (_, _) => new ReadNotificationCommand(Guid.NewGuid())),
        new(typeof(ReadAllNotificationsCommand), false, (_, _) => new ReadAllNotificationsCommand()),
        new(typeof(SnoozeNotificationCommand), false, (_, _) => new SnoozeNotificationCommand(Guid.NewGuid(), null, SnoozePreset.Week)),
        new(typeof(DismissNotificationCommand), false, (_, _) => new DismissNotificationCommand(Guid.NewGuid())),
        new(typeof(EvaluateNotificationsCommand), false, (_, _) => new EvaluateNotificationsCommand()),
        new(typeof(DispatchNotificationsCommand), false, (_, _) => new DispatchNotificationsCommand()),
        new(typeof(SendDigestsCommand), false, (_, _) => new SendDigestsCommand()),
        new(typeof(TestChannelCommand), false, (_, _) => new TestChannelCommand("email")),
        new(typeof(GetAdminNotificationsQuery), false, (_, _) => new GetAdminNotificationsQuery()),
        new(typeof(AdminTestChannelCommand), false, (_, _) => new AdminTestChannelCommand("email")),
        new(typeof(SetDiscordWebhookCommand), false, (_, _) => new SetDiscordWebhookCommand("https://discord.com/api/webhooks/1/x")),
        new(typeof(RemoveDiscordWebhookCommand), false, (_, _) => new RemoveDiscordWebhookCommand()),
        new(typeof(SetHouseholdWebhookCommand), false, (_, _) => new SetHouseholdWebhookCommand("https://discord.com/api/webhooks/1/x")),
        new(typeof(RemoveHouseholdWebhookCommand), false, (_, _) => new RemoveHouseholdWebhookCommand()),
        new(typeof(GetNotificationPreferencesQuery), false, (_, _) => new GetNotificationPreferencesQuery()),
        new(typeof(SaveNotificationPreferencesCommand), false, (_, _) => new SaveNotificationPreferencesCommand(
            false, ActionMode.Immediate, InfoMode.Digest, DigestFrequency.Weekly, 1, new Dictionary<string, int>(), 0)),

        // The export is the reader's own view of what they can read; its views expose every exported item to the
        // output guard (1D2 design §6), and ExportPrivacyTests checks the content.
        new(typeof(ExportManifestQuery), false, (_, _) => new ExportManifestQuery()),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("subscriptions", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("envelopes", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("income_sources", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("income_phases", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("properties", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("property_recurring_costs", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("tenancies", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("loans", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("loan_phases", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("loan_insurances", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("loan_fees", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("loan_events", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("loan_bank_schedule", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("valuations", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("flows", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("holdings", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("holding_snapshots", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("holding_movements", null, null)),
        new(typeof(ExportPageQuery), false, (_, _) => new ExportPageQuery("goals", null, null)),

        new(typeof(ListEnvelopesQuery), false, (_, _) => new ListEnvelopesQuery(VisibilityScope.Me, true)),
        new(typeof(GetInvestmentsOverviewQuery), false, (_, _) => new GetInvestmentsOverviewQuery(VisibilityScope.Me, OverviewSpan.All)),
        new(typeof(GetInvestmentsPerformanceQuery), false, (_, _) => new GetInvestmentsPerformanceQuery(VisibilityScope.Me, new LocalDate(2024, 12, 31), new LocalDate(2025, 10, 15))),
        new(typeof(GetInvestmentsHoldingsQuery), false, (_, _) => new GetInvestmentsHoldingsQuery(VisibilityScope.Me)),
        new(typeof(GetRoutineQuery), false, (_, _) => new GetRoutineQuery(null)),
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
        new(typeof(AddHoldingCommand), true, (_, item) => new AddHoldingCommand(item, new HoldingDraft("Fonds", null, "fund", null, null, null, null, null, null, null, null))),
        new(typeof(EditHoldingCommand), true, (_, item) => new EditHoldingCommand(item, Guid.NewGuid(), new HoldingDraft("Fonds", null, "fund", null, null, null, null, null, null, null, null), 1)),
        new(typeof(DeleteHoldingCommand), true, (_, item) => new DeleteHoldingCommand(item, Guid.NewGuid(), 1)),
        new(typeof(AcceptHoldingPriceCommand), true, (_, item) => new AcceptHoldingPriceCommand(item, Guid.NewGuid(), new LocalDate(2025, 1, 1))),
        new(typeof(RecordHoldingsSnapshotCommand), true, (_, item) => new RecordHoldingsSnapshotCommand(item, null,
            new SnapshotDraft(new LocalDate(2025, 1, 1), null, true, [new SnapshotLineDraft(Guid.NewGuid(), null, null, Money.Create(1m, Currency.Eur).Ok(), null)], false))),
        new(typeof(DeleteHoldingsSnapshotCommand), true, (_, item) => new DeleteHoldingsSnapshotCommand(item, Guid.NewGuid())),
        new(typeof(GetHoldingsQuery), true, (_, item) => new GetHoldingsQuery(item, null)),
        new(typeof(ListHoldingsSnapshotsQuery), true, (_, item) => new ListHoldingsSnapshotsQuery(item)),
        new(typeof(AddHoldingMovementCommand), true, (_, item) => new AddHoldingMovementCommand(item, Guid.NewGuid(),
            new MovementDraft(new LocalDate(2025, 1, 1), "fee", null, null, Money.Create(1m, Currency.Eur).Ok(), null, null, null))),
        new(typeof(RecordSwitchCommand), true, (_, item) => new RecordSwitchCommand(item, new SwitchDraft(new LocalDate(2025, 1, 1), false,
            [new SwitchLegDraft(Guid.NewGuid(), Money.Create(1m, Currency.Eur).Ok(), null, null)], [new SwitchLegDraft(Guid.NewGuid(), Money.Create(1m, Currency.Eur).Ok(), null, null)], null))),
        new(typeof(ListHoldingMovementsQuery), true, (_, item) => new ListHoldingMovementsQuery(item, Guid.NewGuid())),
        new(typeof(GetHoldingSeriesQuery), true, (_, item) => new GetHoldingSeriesQuery(item, Guid.NewGuid())),
        new(typeof(GetHoldingPerformanceQuery), true, (_, item) => new GetHoldingPerformanceQuery(item, Guid.NewGuid(), new LocalDate(2025, 1, 1), new LocalDate(2025, 6, 30))),
        new(typeof(SaveCostReportCommand), true, (_, item) => new SaveCostReportCommand(item, Guid.NewGuid(), 2024,
            new CostReportDraft(8m, 2.5m, 5.5m, null, null, null, null, null))),
        new(typeof(GetCostReportQuery), true, (_, item) => new GetCostReportQuery(item, Guid.NewGuid(), 2024)),
        new(typeof(ListCostReportsQuery), true, (_, item) => new ListCostReportsQuery(item, Guid.NewGuid())),

        new(typeof(RecordRoutineCommand), false, (_, _) => new RecordRoutineCommand(new LocalDate(2025, 1, 31), [])),
        new(typeof(UndoRoutineCommand), false, (_, _) => new UndoRoutineCommand([])),

        new(typeof(ListIncomeSourcesQuery), false, (_, _) => new ListIncomeSourcesQuery(VisibilityScope.Me, true)),
        new(typeof(GetIncomeOverviewQuery), false, (_, _) => new GetIncomeOverviewQuery(VisibilityScope.Me, OverviewPeriod.Year, null, null, null, true)),
        new(typeof(GetDashboardQuery), false, (_, _) => new GetDashboardQuery(VisibilityScope.Me)),
        new(typeof(GetNetWorthHistoryQuery), false, (_, _) => new GetNetWorthHistoryQuery(VisibilityScope.Me, NetWorthPeriod.All)),
        new(typeof(SaveAllocationTargetCommand), false, (_, _) => new SaveAllocationTargetCommand(new Dictionary<string, decimal>())),
        new(typeof(GetTaxEstimateQuery), false, (_, _) => new GetTaxEstimateQuery(2025, VisibilityScope.Me)),
        new(typeof(GetRental2044Query), false, (_, _) => new GetRental2044Query(2025, VisibilityScope.Me)),
        new(typeof(GetSaleTaxQuery), true, (_, item) => new GetSaleTaxQuery(VisibilityScope.Me, item)),
        new(typeof(SaveTaxProfileCommand), false, (_, _) => new SaveTaxProfileCommand(2025, TaxProfile.Empty, VisibilityScope.Me)),
        new(typeof(SimulateTaxQuery), false, (_, _) => new SimulateTaxQuery(new TaxInputs(
            2025, [new TaxDeclarant("bench", 1m, EmploymentStatus.NonExecutive, 0m, 0m)], 0, 0, 0m, 0m, 0m, null, 0m, 0m, 0m, 0m, 0m, 0m, 0m))),
        // Scenario spec §5: the scenario requests run against the scenario twins; a replace may name a funding envelope, which the
        // actor only has to read (the envelope twin of the other row). A creation has no target, so it is not on the second axis:
        // ScenarioUseCaseTests checks its funding refusals. Delete archives its twin, so it comes after the other twin requests.
        new(typeof(ListScenariosQuery), false, (_, _) => new ListScenariosQuery(VisibilityScope.Me)),
        new(typeof(SimulateScenarioQuery), false, (_, _) => new SimulateScenarioQuery(VisibilityScope.Me, BenchScenario([]))),
        new(typeof(GetScenarioDefaultsQuery), false, (_, _) => new GetScenarioDefaultsQuery(VisibilityScope.Me)),
        new(typeof(CreateScenarioCommand), false, (d, _) => new CreateScenarioCommand("bench", Visibility.Shared,
            [new OwnerShare(d.App.AdultA, 10_000)], VisibilityScope.Me, BenchScenario([]))),
        new(typeof(GetScenarioQuery), true, (_, item) => new GetScenarioQuery(item)),
        // The twin keeps its visibility: a private twin turned shared would change what the rest of the matrix expects.
        new(typeof(ReplaceScenarioCommand), true, (d, item) => new ReplaceScenarioCommand(item, "bench", d.VisibilityOf(item), VisibilityScope.Me, BenchScenario([])))
        {
            BuildNamingAnother = (d, target, other) => Task.FromResult<object>(new ReplaceScenarioCommand(
                target, "bench", d.VisibilityOf(target), VisibilityScope.Me, BenchScenario([new FundingLine(d.EnvelopeTwinOf(other), 100m)]))),
            SecondItemAccess = AccessKind.Read,
        },
        new(typeof(DeleteScenarioCommand), true, (_, item) => new DeleteScenarioCommand(item)),
        new(typeof(GetIncomeSourceQuery), true, (_, item) => new GetIncomeSourceQuery(item)),
        new(typeof(EditIncomeSourceCommand), true, (_, item) => new EditIncomeSourceCommand(item, null, null, new IncomeSourceChange(Note: "bench"))),
        new(typeof(CreateIncomeSourceCommand), false, (d, _) => new CreateIncomeSourceCommand("bench", "salary", Currency.Eur, Visibility.Shared,
            [new OwnerShare(d.App.AdultA, 10_000)], null, null,
            [new PhaseDraft("bench", new LocalDate(2025, 1, 1), null, Money.Create(1m, Currency.Eur).Ok(), Duration.Create(1, DurationUnit.Month).Ok(), null, null, null)])),

        // A phase id is looked up on the route's source only (IPhaseStore by source), so a phase id alone can never
        // reach another item: no BuildNamingAnother, as the envelope's facts.
        new(typeof(AddIncomePhaseCommand), true, (_, item) => new AddIncomePhaseCommand(item,
            new PhaseDraft("bench", new LocalDate(2025, 1, 1), null, Money.Create(1m, Currency.Eur).Ok(), Duration.Create(1, DurationUnit.Month).Ok(), null, null, null))),
        new(typeof(EditIncomePhaseCommand), true, (_, item) => new EditIncomePhaseCommand(item, Guid.NewGuid(),
            new PhaseDraft("bench", new LocalDate(2025, 1, 1), null, Money.Create(1m, Currency.Eur).Ok(), Duration.Create(1, DurationUnit.Month).Ok(), null, null, null), 1)),
        new(typeof(DeleteIncomePhaseCommand), true, (_, item) => new DeleteIncomePhaseCommand(item, Guid.NewGuid(), 1)),
        new(typeof(ChangeIncomePhaseCommand), true, (_, item) => new ChangeIncomePhaseCommand(item, "Salaire", new LocalDate(2025, 6, 1),
            Money.Create(1m, Currency.Eur).Ok(), null, null, null)),
        new(typeof(GetIncomeExpectedQuery), true, (_, item) => new GetIncomeExpectedQuery(item, new LocalDate(2025, 1, 1), new LocalDate(2025, 12, 31))),
        new(typeof(GetIncomeSourceOverviewQuery), true, (_, item) => new GetIncomeSourceOverviewQuery(item, "12m")),

        // A flow id is looked up on the route's source only (IFlows.ListAsync by item), so a flow id alone can never reach another item.
        new(typeof(RecordIncomeOneOffCommand), true, (_, item) => new RecordIncomeOneOffCommand(item, new LocalDate(2025, 1, 1),
            Money.Create(1m, Currency.Eur).Ok(), null)),
        new(typeof(CorrectIncomeOneOffCommand), true, (_, item) => new CorrectIncomeOneOffCommand(item, Guid.NewGuid(), new LocalDate(2025, 1, 1),
            Money.Create(1m, Currency.Eur).Ok(), null)),
        new(typeof(DeleteIncomeOneOffCommand), true, (_, item) => new DeleteIncomeOneOffCommand(item, Guid.NewGuid())),
        new(typeof(CloseIncomeSourceCommand), true, (_, item) => new CloseIncomeSourceCommand(item, new LocalDate(2020, 1, 1))),
        new(typeof(ReopenIncomeSourceCommand), true, (_, item) => new ReopenIncomeSourceCommand(item)),

        new(typeof(ListGoalsQuery), false, (_, _) => new ListGoalsQuery(VisibilityScope.Me, true)),
        new(typeof(GetGoalQuery), true, (_, item) => new GetGoalQuery(item, VisibilityScope.Me)),
        new(typeof(CreateGoalCommand), false, (d, _) => new CreateGoalCommand("bench", GoalKinds.Savings, 1000m, null, null, null, [],
            Visibility.Shared, [new OwnerShare(d.App.AdultA, 10_000)])),
        // Version 1 is never a row's xmin: an allowed writer meets goals.conflict, never an access refusal.
        new(typeof(EditGoalCommand), true, (_, item) => new EditGoalCommand(item, "bench", 1000m, null, null, null, [], 1))
        {
            // D10: a goal links an envelope its actor sees; the bench names the envelope twin of the other row, at 1 bp so that the
            // links its successful runs leave never fill an envelope. The goal's current version, so an allowed edit succeeds.
            BuildNamingAnother = async (d, target, other) =>
            {
                await using var scope = d.App.Services.CreateAsyncScope();
                var version = (await scope.ServiceProvider.GetRequiredService<IGoalStore>().FindAsync(target, default))!.Version;
                return new EditGoalCommand(target, "bench", 1000m, null, null, null, [new FundingShare(d.EnvelopeTwinOf(other), 1)], version);
            },
            SecondItemAccess = AccessKind.Read,
        },
        new(typeof(CloseGoalCommand), true, (_, item) => new CloseGoalCommand(item)),
        new(typeof(ReopenGoalCommand), true, (_, item) => new ReopenGoalCommand(item)),
        new(typeof(PutGoalProgressCommand), true, (_, item) => new PutGoalProgressCommand(item, new LocalDate(2025, 1, 1), 100m)),
        new(typeof(DeleteGoalProgressCommand), true, (_, item) => new DeleteGoalProgressCommand(item, new LocalDate(2025, 1, 1))),

        // A document id is not an item: get, content, edit and archive take the row's seeded document, on every row, and are checked
        // against the document's own rules (LeakBenchTests). A core.item target makes upload, link, role and unlink item-scoped.
        new(typeof(ListDocumentsQuery), false, (_, _) => new ListDocumentsQuery(VisibilityScope.Me, true, false, new DocumentTarget(TargetKinds.CoreHousehold, TargetKinds.Household))),
        // Every item the actor sees in « Moi », by name: the scan checks the other adult's private label never comes out.
        new(typeof(ListDocumentTargetsQuery), false, (_, _) => new ListDocumentTargetsQuery()),
        // As the link below, writing nothing: an allowed writer meets documents.already_linked, never an access refusal.
        new(typeof(PreviewDocumentLinkQuery), true, (d, item) => new PreviewDocumentLinkQuery(d.Documents[item],
            new DocumentTarget(TargetKinds.CoreItem, item.Value))),
        new(typeof(GetDocumentQuery), false, (d, item) => new GetDocumentQuery(d.Documents[item])) { OnEveryRow = true },
        new(typeof(GetDocumentContentQuery), false, (d, item) => new GetDocumentContentQuery(d.Documents[item])) { OnEveryRow = true },
        new(typeof(EditDocumentCommand), false, (d, item) => new EditDocumentCommand(d.Documents[item], "bench", null, null)) { OnEveryRow = true },
        new(typeof(SetDocumentArchivedCommand), false, (d, item) => new SetDocumentArchivedCommand(d.Documents[item], true)) { OnEveryRow = true },
        new(typeof(UploadDocumentCommand), true, (_, item) => new UploadDocumentCommand(DocumentEndpointTests.Pdf, "bench.pdf", "bench", null, null,
            new DocumentTarget(TargetKinds.CoreItem, item.Value), DocumentRoles.Statement)),
        // The row's document is already linked to the row: an allowed writer meets documents.already_linked, never an access refusal.
        new(typeof(LinkDocumentCommand), true, (d, item) => new LinkDocumentCommand(d.Documents[item],
            new DocumentTarget(TargetKinds.CoreItem, item.Value), DocumentRoles.Statement)),
        new(typeof(SetLinkRoleCommand), true, (d, item) => new SetLinkRoleCommand(d.Documents[item],
            new DocumentTarget(TargetKinds.CoreItem, item.Value), DocumentRoles.Other)),
        // A document of its own: the first allowed unlink removes the row's only link, and the second allowed actor meets a document it
        // no longer reads (« document » not found, not « item »); no other entry meets an unlinked document.
        new(typeof(UnlinkDocumentCommand), true, (d, item) => new UnlinkDocumentCommand(d.UnlinkDocuments[item],
            new DocumentTarget(TargetKinds.CoreItem, item.Value))),

        new(typeof(ListLoansQuery), false, (_, _) => new ListLoansQuery(VisibilityScope.Me, true)),
        new(typeof(GetLoansOverviewQuery), false, (_, _) => new GetLoansOverviewQuery(VisibilityScope.Me)),
        // Names its loan: one the actor cannot read is refused at loanId like an unknown one, never read.
        new(typeof(SimulateLoansQuery), false, (_, item) => new SimulateLoansQuery(VisibilityScope.Me, new LocalDate(2025, 11, 15), 1_000m,
            SplitRule.Chosen, item, RepaymentEffect.ReduceTerm, [])),
        new(typeof(GetLoanQuery), true, (_, item) => new GetLoanQuery(item)),
        new(typeof(GetLoanScheduleQuery), true, (_, item) => new GetLoanScheduleQuery(item)),
        new(typeof(SimulateLoanRepaymentQuery), true, (_, item) => new SimulateLoanRepaymentQuery(item, new LocalDate(2025, 1, 15), 1_000m, null, 0m)),
        new(typeof(CreateLoanCommand), false, (d, _) => new CreateLoanCommand("bench", Currency.Eur, Visibility.Shared,
            [new OwnerShare(d.App.AdultA, 10_000)], null, null, BenchContract)),
        new(typeof(EditLoanCommand), true, (_, item) => new EditLoanCommand(item, null, new LoanDetailsChange(Note: "bench")))
        {
            // Spec 3C1 §3: a loan links to a property its actor reads; the bench names the property twin of the other row.
            BuildNamingAnother = (d, target, other) => Task.FromResult<object>(
                new EditLoanCommand(target, null, new LoanDetailsChange(PropertyId: d.PropertyTwinOf(other)))),
            SecondItemAccess = AccessKind.Read,
        },
        // Version 1 is never a row's xmin: an allowed writer meets realestate.loan_conflict, never an access refusal.
        new(typeof(ReplaceLoanContractCommand), true, (_, item) => new ReplaceLoanContractCommand(item, BenchContract, 1)),

        // A fee or repayment id is looked up in the route's loan only (StoredLoan's own lists), so an id alone can never
        // reach another item: no BuildNamingAnother, as the income phases.
        new(typeof(AddLoanFeeCommand), true, (_, item) => new AddLoanFeeCommand(item, new FeeDraft(FeeKind.File, 100m, new LocalDate(2025, 1, 15), null))),
        new(typeof(EditLoanFeeCommand), true, (_, item) => new EditLoanFeeCommand(item, Guid.NewGuid(),
            new FeeDraft(FeeKind.File, 100m, new LocalDate(2025, 1, 15), null), 1)),
        new(typeof(DeleteLoanFeeCommand), true, (_, item) => new DeleteLoanFeeCommand(item, Guid.NewGuid(), 1)),
        new(typeof(AddLoanRepaymentCommand), true, (_, item) => new AddLoanRepaymentCommand(item,
            new RepaymentDraft(new LocalDate(2025, 3, 15), 100m, RepaymentEffect.ReduceTerm, null, null))),
        new(typeof(EditLoanRepaymentCommand), true, (_, item) => new EditLoanRepaymentCommand(item, Guid.NewGuid(),
            new RepaymentDraft(new LocalDate(2025, 3, 15), 100m, RepaymentEffect.ReduceTerm, null, null), 1)),
        new(typeof(DeleteLoanRepaymentCommand), true, (_, item) => new DeleteLoanRepaymentCommand(item, Guid.NewGuid(), 1)),
        // 3F2: the bank's table is the route's loan's own and its text names no item: no BuildNamingAnother.
        new(typeof(PreviewBankScheduleQuery), true, (_, item) => new PreviewBankScheduleQuery(item, BenchTable)),
        // Version 1 is never a row's xmin: an allowed writer meets realestate.loan_conflict, never an access refusal.
        new(typeof(ReplaceBankScheduleCommand), true, (_, item) => new ReplaceBankScheduleCommand(item, BenchTable, 1)),
        // 4D2: a proposal's rows; dated on no bench loan's due date, so an allowed writer meets realestate.bank_schedule_invalid, never a write.
        new(typeof(ReplaceBankRowsCommand), true, (_, item) => new ReplaceBankRowsCommand(item, [BenchRow], false)),
        new(typeof(DeleteBankScheduleCommand), true, (_, item) => new DeleteBankScheduleCommand(item, 1)),

        new(typeof(ListPropertiesQuery), false, (_, _) => new ListPropertiesQuery(VisibilityScope.Me)),
        new(typeof(GetPropertiesOverviewQuery), false, (_, _) => new GetPropertiesOverviewQuery(VisibilityScope.Me, PropertiesSpan.All)),
        new(typeof(GetPropertiesRentalQuery), false, (_, _) => new GetPropertiesRentalQuery(VisibilityScope.Me, null)),
        new(typeof(GetPropertiesCostsQuery), false, (_, _) => new GetPropertiesCostsQuery(VisibilityScope.Me)),
        new(typeof(GetPropertiesValuationQuery), false, (_, _) => new GetPropertiesValuationQuery(VisibilityScope.Me)),
        new(typeof(GetPropertyRoutineQuery), false, (_, _) => new GetPropertyRoutineQuery(null)),
        // 3E2: the routine names its properties in its lines; each one's write check is the bench fact below.
        new(typeof(RecordPropertyRoutineCommand), false, (_, _) => new RecordPropertyRoutineCommand(new LocalDate(2025, 1, 31), [], [])),
        new(typeof(UndoPropertyRoutineCommand), false, (_, _) => new UndoPropertyRoutineCommand([])),
        new(typeof(GetPropertyQuery), true, (_, item) => new GetPropertyQuery(item)),
        new(typeof(CreatePropertyCommand), false, (d, _) => new CreatePropertyCommand("bench", Currency.Eur, Visibility.Shared,
            [new OwnerShare(d.App.AdultA, 10_000)], BenchProperty)),
        // Version 1 is never a row's xmin: an allowed writer meets realestate.property_conflict, never an access refusal.
        new(typeof(EditPropertyCommand), true, (_, item) => new EditPropertyCommand(item, null, new PropertyChange(Note: "bench"), 1)),

        // A fact id is looked up by (id, item_id) in the Core (2A), as the envelopes': no BuildNamingAnother.
        new(typeof(RecordPropertyValuationCommand), true, (_, item) => new RecordPropertyValuationCommand(item, new LocalDate(2025, 1, 1), 1m, null)),
        new(typeof(VoidPropertyValuationCommand), true, (_, item) => new VoidPropertyValuationCommand(item, Guid.NewGuid())),
        new(typeof(RecordPropertyFlowCommand), true, (_, item) => new RecordPropertyFlowCommand(item, BenchFlow)),
        new(typeof(CorrectPropertyFlowCommand), true, (_, item) => new CorrectPropertyFlowCommand(item, Guid.NewGuid(), BenchFlow)),
        new(typeof(VoidPropertyFlowCommand), true, (_, item) => new VoidPropertyFlowCommand(item, Guid.NewGuid())),

        // A cost or tenancy id is looked up in the route's property only (StoredProperty's own lists), so an id alone can never
        // reach another item: no BuildNamingAnother, as the loan's fees.
        new(typeof(AddPropertyRecurringCostCommand), true, (_, item) => new AddPropertyRecurringCostCommand(item, BenchCost)),
        new(typeof(EditPropertyRecurringCostCommand), true, (_, item) => new EditPropertyRecurringCostCommand(item, Guid.NewGuid(), BenchCost, 1)),
        new(typeof(DeletePropertyRecurringCostCommand), true, (_, item) => new DeletePropertyRecurringCostCommand(item, Guid.NewGuid(), 1)),
        new(typeof(AddPropertyTenancyCommand), true, (_, item) => new AddPropertyTenancyCommand(item, BenchTenancy)),
        new(typeof(EditPropertyTenancyCommand), true, (_, item) => new EditPropertyTenancyCommand(item, Guid.NewGuid(), BenchTenancy, 1)),
        new(typeof(DeletePropertyTenancyCommand), true, (_, item) => new DeletePropertyTenancyCommand(item, Guid.NewGuid(), 1)),
        // Rental spec §3.3: a revision is looked up in the route's property only, as its tenancy.
        new(typeof(GetTenancyRevisionQuery), true, (_, item) => new GetTenancyRevisionQuery(item, Guid.NewGuid())),
        new(typeof(GetRentReceiptQuery), true, (_, item) => new GetRentReceiptQuery(item, null)),
        new(typeof(AcceptRentRevisionCommand), true, (_, item) => new AcceptRentRevisionCommand(item, Guid.NewGuid(), new LocalDate(2025, 9, 1))),
        new(typeof(UndoRentRevisionCommand), true, (_, item) => new UndoRentRevisionCommand(item, Guid.NewGuid())),
        new(typeof(GetPropertyArrearsQuery), true, (_, item) => new GetPropertyArrearsQuery(item)),
        new(typeof(GetRentArrearsQuery), false, (_, _) => new GetRentArrearsQuery(VisibilityScope.Me)),
        new(typeof(GetRentRevisionsQuery), false, (_, _) => new GetRentRevisionsQuery(VisibilityScope.Me)),
    ];

    /// <summary>A scenario that computes: 100 000 borrowed at 3 % over 20 years, from 2025-12-01.</summary>
    internal static PropertyPurchase BenchScenario(IReadOnlyList<FundingLine> funding) => new(
        1, PurchaseUse.Primary, new LocalDate(2025, 12, 1), 100_000m, 0m, 0m, 0m, 0m, 0m, 0m, 0, null, funding,
        [new ScenarioLoan("bench", 100_000m, 0.03m, 240, null, new ScenarioInsurance(ScenarioInsuranceKind.FixedMonthly, 0m), 0m, 0m)],
        new ScenarioAssumptions(10, ScenarioRules.Defaults, null, 0m, AllocationClasses.Cash));

    /// <summary>A small loan that computes: 1 000 at 1 % over 12 payments from 2025-02-15.</summary>
    private static LoanContract BenchContract => new(1_000m, new LocalDate(2025, 1, 15), new LocalDate(2025, 2, 15), 12,
        InterestRounding.HalfUp, FirstPeriod.Full, [new LoanPhaseEntry(new LoanPhase(1, PhaseMode.Annuity, 0.01m, RateType.Fixed), null)], []);

    /// <summary>One row of a bank's table, as pasted: it may not fit a bench loan — the bench checks access, not merits.</summary>
    private const string BenchTable = "15/02/2025;84,25;0,83;83,42;916,58";

    /// <summary>A bank row on 2025-02-16: the bench loans fall due on the 15th, so it never fits one.</summary>
    private static BankRow BenchRow => new(new LocalDate(2025, 2, 16), 84.25m, 0.83m, 83.42m, null, 916.58m);

    /// <summary>A rental bought 100 000 on 2020-01-15, nothing else.</summary>
    private static PropertyDetails BenchProperty => new(PropertyType.Rental, new LocalDate(2020, 1, 15), 100_000m, 0m, 0m, 0m,
        null, null, PropertyCadence.Yearly, null, null, null);

    private static PropertyFlowDraft BenchFlow => new(new LocalDate(2025, 1, 1), "expense", 1m, PropertyCategories.Works, null);

    private static RecurringCostDraft BenchCost => new("property_tax", 1m, 1, DurationUnit.Year, new LocalDate(2025, 1, 1), null, null);
    /// <summary>After the twins' purchase (2020-01-15); a writer's second run meets realestate.tenancy_overlap, never an access refusal.</summary>
    private static Tenancy BenchTenancy => new("bench", new LocalDate(2025, 1, 1), null, 1m, 0m, null, null);

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
