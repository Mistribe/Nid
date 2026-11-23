using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Banking.Application;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Core.Application.Fx;
using Nid.Core.Application.Import;
using Nid.Core.Contracts;
using Nid.Core.Application.Inflation;
using Nid.Core.Domain;
using Nid.Core.Infrastructure.Fx;
using Nid.Core.Infrastructure.Persistence;
using Nid.Host.Admin;
using Nid.Host.Jobs;
using Nid.Documents.Application;
using Nid.Documents.Infrastructure.Persistence;
using Nid.Goals.Application;
using Nid.Childcare.Infrastructure.Persistence;
using Nid.Goals.Infrastructure.Persistence;
using Nid.Income.Application.Import;
using Nid.Income.Contracts;
using Nid.Income.Infrastructure.Persistence;
using Nid.Investments.Application.Import;
using Nid.Investments.Contracts;
using Nid.Investments.Infrastructure.Persistence;
using Nid.Platform;
using Nid.Platform.Admin;
using Nid.Platform.Audit;
using Nid.Platform.Contracts;
using Nid.Platform.Export;
using Nid.Platform.Import;
using Nid.Platform.Infrastructure.Assets;
using Nid.Platform.Infrastructure.Audit;
using Nid.Platform.Infrastructure.Persistence;
using Nid.Platform.Notifications;
using UnambitiousFx.Myelin;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Infrastructure.Persistence;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Application.Import;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Infrastructure.Catalog;
using Nid.Subscriptions.Infrastructure.Persistence;
using NodaTime;
using Npgsql;
using UnambitiousFx.Synapse;

namespace Nid.Host;

public static class NidServiceCollectionExtensions
{
    public static IServiceCollection AddNid(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton<IClock>(SystemClock.Instance);

        // One data source for the process, one connection per request shared by every module's context, so
        // IUnitOfWork can span them. A context handed an external connection no longer builds its own data
        // source: NodaTime is configured on the data source AND on each context (Npgsql EF documentation,
        // "Using an external NpgsqlDataSource").
        // Built by a factory, not registered as an instance: the container disposes what it creates, so the
        // pool goes with the provider (an instance handed in is never disposed by it).
        services.AddSingleton(_ =>
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            // Password authentication only: without this, Npgsql probes GSS encryption and the chiseled image, which has no
            // Kerberos library, prints "Cannot load library libgssapi_krb5.so.2" (in `nid admin`'s output too).
            dataSourceBuilder.ConnectionStringBuilder.GssEncryptionMode = GssEncryptionMode.Disable;
            dataSourceBuilder.UseNodaTime();
            return dataSourceBuilder.Build();
        });
        services.AddScoped<SharedConnection>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // Audit (1F3a): each context saves its own log lines in its own transaction (spec §4.2).
        services.AddScoped<Nid.Platform.Audit.AuditRequest>();
        services.AddScoped<AuditInterceptor>();
        services.AddSingleton<IAuditMap, CoreAuditMap>();
        services.AddSingleton<IAuditMap, SubscriptionsAuditMap>();
        services.AddSingleton<IAuditMap, InvestmentsAuditMap>();
        services.AddSingleton<IAuditMap, IncomeAuditMap>();
        services.AddSingleton<IAuditMap, GoalsAuditMap>();
        services.AddSingleton<IAuditMap, Nid.Ai.Infrastructure.Persistence.AiAuditMap>();
        services.AddSingleton<IAuditMap, Nid.Childcare.Infrastructure.Persistence.ChildcareAuditMap>();
        services.AddSingleton<IAuditMap, DocumentsAuditMap>();
        services.AddSingleton<IAuditMap, RealEstateAuditMap>();
        services.AddSingleton<IAuditMap, BankingAuditMap>();
        services.AddSingleton<IAuditMap, PlatformAuditMap>();

        // #23: an item delete asks every module's part.
        services.AddScoped<IItemDeletions, Nid.Core.Application.ItemDeletions>();
        services.AddScoped<IApprovingProposal, Nid.Core.Application.ApprovingProposal>();
        services.AddScoped<IItemDeletionPart, CoreDeletionPart>();
        services.AddScoped<IPersonUsePart, Nid.Core.Infrastructure.Persistence.CorePersonUsePart>();

        services.AddDbContext<CoreDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<SharedConnection>().Connection, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", CoreDbContext.Schema);
        }).AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));
        services.AddScoped<ITransactionParticipant, DbContextParticipant<CoreDbContext>>();
        services.AddScoped<IPersonRegistry, PersonRegistry>();
        services.AddScoped<IPersonalAccessTokens, PersonalAccessTokenStore>();
        services.AddScoped<ILocalAccountStore, LocalAccountStore>();
        services.AddScoped<IProposalStore, ProposalStore>();
        services.AddScoped<Nid.Core.Application.ProposalHandlers>();
        services.AddScoped<IProposalSubmitter, Nid.Core.Application.ProposalSubmitter>();
        services.AddScoped<IProposalHandler, Nid.Core.Application.PrivateRequestProposal>();
        services.AddScoped<IItemRegistry, ItemRegistry>();
        services.AddScoped<IAccessPolicy, AccessPolicy>();
        services.AddScoped<IHouseholdCalendar, HouseholdCalendar>();
        services.AddScoped<Nid.Core.Application.Dashboard.DashboardReader>();
        services.AddScoped<IMonthBudget>(sp => sp.GetRequiredService<Nid.Core.Application.Dashboard.DashboardReader>());
        services.AddScoped<Nid.Core.Application.Dashboard.MonthChangesReader>();
        services.AddSingleton<Nid.Core.Application.Dashboard.MonthChangeWords>();
        services.AddScoped<Nid.Platform.Contracts.INotificationSource, Nid.Core.Application.Dashboard.CoreNotificationSource>();
        services.AddScoped<FactGate>();
        services.AddScoped<IValuations, Valuations>();
        services.AddScoped<IFlows, Flows>();
        services.AddScoped<IFactImports, FactImports>();
        services.AddSingleton<IItemRules, ItemRules>();
        services.AddScoped<IVisibleItems, VisibleItems>();
        services.AddScoped<IActorAccessor, ActorAccessor>();
        services.AddSingleton<LoggingAccessViolations>();
        services.AddScoped<IAccessViolations, AuditedAccessViolations>();
        services.AddScoped<ISecurityEvents, SecurityEventWriter>();
        services.AddScoped<IAuditLog, AuditLogReader>();
        services.AddScoped<IImportAudit, ImportAuditLog>();
        services.AddScoped<Nid.Host.Auth.OidcSignIn>();
        services.AddSingleton<AccessDeniedThrottle>();
        services.AddSingleton<Nid.Platform.Notifications.TestSendThrottle>();
        services.AddScoped<IAccessDenials, AccessDenials>();
        services.AddSingleton<Nid.Host.Auth.TokenFailureGate>();
        services.AddSingleton(new EnabledModules(["core", "subscriptions", "import", "investments", "income", "realestate", "goals", "documents", "banking", "childcare"]));

        // Exchange rates (1C2a). The options are bound by Program from configuration before AddNid; the defaults
        // below only apply when nothing was bound (tests, tools).
        services.TryAddSingleton(new FxOptions());
        services.AddMyelin(NidOutbound.Apply);
        services.TryAddSingleton<IFxSourceClient, FxSourceClient>();
        services.TryAddSingleton(new PriceOptions());
        services.TryAddSingleton<IPriceSourceClient, Nid.Investments.Infrastructure.Prices.PriceSourceClient>();
        services.TryAddSingleton(new Nid.Banking.Contracts.BankingOptions());
        services.TryAddSingleton<Nid.Banking.Contracts.IEnableBankingClient, Nid.Banking.Infrastructure.EnableBanking.EnableBankingClient>();
        services.TryAddSingleton(new IrlOptions());
        services.TryAddSingleton<IIrlSourceClient, Nid.RealEstate.Infrastructure.Irl.IrlSourceClient>();
        services.TryAddSingleton<IIrlJobTrigger, JobsDisabledIrlTrigger>();
        services.TryAddSingleton(new InflationOptions());
        services.TryAddSingleton<ICpiSourceClient, Nid.Core.Infrastructure.Inflation.CpiSourceClient>();
        services.TryAddSingleton<ICpiJobTrigger, JobsDisabledCpiTrigger>();
        services.TryAddSingleton<IFxJobTrigger, JobsDisabledFxTrigger>();
        services.TryAddSingleton<IPriceJobTrigger, JobsDisabledPriceTrigger>();
        services.AddScoped<IFxRateStore, FxRateStore>();
        services.AddScoped<IFxRunLog, FxRunLog>();
        services.AddScoped<Nid.Platform.Contracts.ISyncHealth, Nid.Core.Infrastructure.Persistence.FxSyncHealth>();
        services.AddScoped<IFxRates, FxRates>();
        services.AddScoped<ISeriesContribution, Nid.Core.Application.Series.FxSeries>();
        services.AddScoped<IHouseholdSettings, HouseholdSettings>();
        services.AddScoped<ITaxProfileStore, TaxProfileStore>();
        services.AddScoped<TaxAssessmentStore>();
        services.AddScoped<ITaxAssessmentStore>(sp => sp.GetRequiredService<TaxAssessmentStore>());
        services.AddScoped<ITaxAssessmentDeclarants>(sp => sp.GetRequiredService<TaxAssessmentStore>());
        services.AddScoped<IScenarioStore, ScenarioStore>();
        services.AddScoped<Nid.Core.Application.Tax.TaxEstimates>();
        services.AddScoped<Nid.Core.Application.Tax.SaleTaxEstimates>();
        services.AddScoped<IRentalTaxOutlook, Nid.Core.Application.Tax.RentalTaxOutlookService>();
        services.AddScoped<ITaxWithholdingRates, Nid.Core.Application.Tax.TaxWithholdingRates>();
        services.AddScoped<ICommitmentsContribution, Nid.Core.Application.Tax.TaxCommitments>();
        services.AddScoped<Nid.Core.Application.Tax.PerDeadline>();
        services.AddScoped<Nid.Core.Application.Tax.TaxCalendarEvents>();
        services.AddScoped<Nid.Core.Application.Scenarios.ScenarioFactsReader>();
        services.AddScoped<Nid.Core.Application.Projection.ProjectionFactsReader>();
        services.AddScoped<Nid.Core.Application.Projection.ProjectionScenarioReader>();
        services.AddScoped<Nid.Core.Application.Scenarios.SaleScenarioReader>();
        services.AddScoped<FxCollector>();

        // Admin area (1G): the job list is Quartz's when AddNidJobs runs, empty otherwise.
        services.AddSingleton(new ProcessStart());
        services.TryAddSingleton<IJobStatusReader, NoJobStatusReader>();
        // File logs off (no FileLogs:Path, or it cannot be written): runs are recorded without logs.
        services.TryAddSingleton(new Nid.Host.Jobs.RunLogFolder(null));
        services.AddSingleton<IJobRunLogs, JobRunLogs>();
        services.TryAddSingleton<IIdentityProviderProbe, OidcIdentityProviderProbe>();
        services.AddScoped<ISystemStatusSource, SystemStatusSource>();

        services.AddDbContext<SubscriptionsDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<SharedConnection>().Connection, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", SubscriptionsDbContext.Schema);
        }).AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));
        services.AddScoped<ITransactionParticipant, DbContextParticipant<SubscriptionsDbContext>>();
        services.AddScoped<ISubscriptionStore, SubscriptionStore>();
        services.AddScoped<Nid.Subscriptions.Contracts.ISubscriptionLabels, Nid.Subscriptions.Infrastructure.Persistence.SubscriptionLabels>();
        services.AddScoped<IItemDeletionPart, SubscriptionsDeletionPart>();
        services.AddScoped<IPersonUsePart, Nid.Subscriptions.Infrastructure.Persistence.SubscriptionsPersonUsePart>();
        services.AddScoped<ICommitmentsContribution, Nid.Subscriptions.Application.SubscriptionsCommitments>();
        services.AddScoped<IExpectedCharges, Nid.Subscriptions.Application.SubscriptionExpectedCharges>();
        services.AddScoped<Nid.Subscriptions.Application.SubscriptionSet>();
        services.AddScoped<ISubscriptionsReview, Nid.Subscriptions.Application.SubscriptionsReview>();
        services.AddScoped<Nid.Subscriptions.Application.SubscriptionsUpcoming>();
        services.AddScoped<IUpcomingEvents>(sp => sp.GetRequiredService<Nid.Subscriptions.Application.SubscriptionsUpcoming>());
        services.AddScoped<Nid.Subscriptions.Application.SubscriptionsOverviewReader>();
        services.AddScoped<Nid.Platform.Contracts.INotificationSource, Nid.Subscriptions.Application.SubscriptionsNotificationSource>();
        services.AddScoped<IProposalHandler, Nid.Subscriptions.Application.InvoiceProposal>();
        services.AddScoped<ISeriesContribution, Nid.Subscriptions.Application.SubscriptionSeries>();

        services.AddDbContext<InvestmentsDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<SharedConnection>().Connection, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", InvestmentsDbContext.Schema);
        }).AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));
        services.AddScoped<ITransactionParticipant, DbContextParticipant<InvestmentsDbContext>>();
        services.AddScoped<IEnvelopeStore, EnvelopeStore>();
        services.AddScoped<IItemDeletionPart, InvestmentsDeletionPart>();
        services.AddScoped<IHoldingStore, HoldingStore>();
        services.AddScoped<ISecurityPriceStore, SecurityPriceStore>();
        services.AddScoped<ISecurityBreakdownStore, SecurityBreakdownStore>();
        services.AddScoped<IAppliedDocumentStamp, Nid.Investments.Application.BreakdownDocumentStamp>();
        services.AddScoped<IPriceRunLog, PriceRunLog>();
        services.AddScoped<Nid.Platform.Contracts.ISyncHealth, Nid.Investments.Infrastructure.Persistence.PriceSyncHealth>();
        services.AddScoped<Nid.Investments.Application.Prices.PriceCollector>();
        services.AddScoped<Nid.Investments.Application.Prices.EnvelopeEstimator>();
        services.AddScoped<Nid.Investments.Application.EnvelopePerformance>();
        services.AddScoped<IEnvelopePerformance>(sp => sp.GetRequiredService<Nid.Investments.Application.EnvelopePerformance>());
        services.AddScoped<INetWorthContribution, Nid.Investments.Application.EnvelopesNetWorth>();
        services.AddScoped<Nid.Investments.Contracts.IEnvelopeFunding, Nid.Investments.Application.EnvelopeFunding>();
        services.AddScoped<Nid.Investments.Contracts.ISavingsRoom, Nid.Investments.Application.SavingsRoom>();
        services.AddScoped<ITaxContribution, Nid.Investments.Application.EnvelopesTax>();
        services.AddScoped<ITaxContribution, Nid.Investments.Application.CapitalTaxContribution>();
        services.AddScoped<ISaleContribution, Nid.Investments.Application.EnvelopesSale>();
        services.AddScoped<Nid.RealEstate.Contracts.IFinancedEnvelopes, Nid.Investments.Application.FinancedEnvelopes>();
        services.AddScoped<Nid.Investments.Application.InvestmentsUpcoming>();
        services.AddScoped<IUpcomingEvents>(sp => sp.GetRequiredService<Nid.Investments.Application.InvestmentsUpcoming>());
        services.AddScoped<Nid.Investments.Application.InvestmentsOverviewReader>();
        services.AddScoped<Nid.Investments.Application.InvestmentsPeriodReader>();
        services.AddScoped<IInvestmentsReview, Nid.Investments.Application.InvestmentsReview>();
        services.AddScoped<ISeriesContribution, Nid.Investments.Application.InvestmentSeries>();
        services.AddScoped<Nid.Platform.Contracts.INotificationSource, Nid.Investments.Application.InvestmentsNotificationSource>();
        services.AddScoped<IProposalHandler, Nid.Investments.Application.ValuationProposal>();
        services.AddScoped<IProposalHandler, Nid.Investments.Application.StatementProposal>();
        services.AddScoped<IProposalHandler, Nid.Investments.Application.HoldingsSnapshotProposal>();
        services.AddScoped<IProposalHandler, Nid.Investments.Application.BreakdownProposal>();

        services.AddDbContext<IncomeDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<SharedConnection>().Connection, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", IncomeDbContext.Schema);
        }).AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));
        services.AddScoped<ITransactionParticipant, DbContextParticipant<IncomeDbContext>>();
        services.AddScoped<ISourceStore, SourceStore>();
        services.AddScoped<IPhaseStore, PhaseStore>();
        services.AddScoped<IOneOffTaxableStore, OneOffTaxableStore>();
        services.AddScoped<Nid.Income.Application.IncomeDocumentUnlinks>();
        services.AddScoped<IItemDeletionPart, IncomeDeletionPart>();
        services.AddScoped<IIncomeContribution, Nid.Income.Application.IncomeSourcesIncome>();
        services.AddScoped<ISeriesContribution, Nid.Income.Application.IncomeSeries>();
        services.AddScoped<Nid.Income.Application.IncomeOverviewEntries>();
        services.AddScoped<IIncomeReview, Nid.Income.Application.IncomeReview>();
        services.AddScoped<Nid.Income.Application.IncomeSourcesUpcoming>();
        services.AddScoped<IUpcomingEvents>(sp => sp.GetRequiredService<Nid.Income.Application.IncomeSourcesUpcoming>());
        services.AddScoped<Nid.Platform.Contracts.INotificationSource, Nid.Income.Application.IncomeNotificationSource>();
        services.AddScoped<IExpectedIncome, Nid.Income.Application.IncomeSourcesNext>();
        services.AddScoped<ITaxContribution, Nid.Income.Application.IncomeSourcesTax>();
        services.AddScoped<IProposalHandler, Nid.Income.Application.PayslipProposal>();

        services.AddDbContext<GoalsDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<SharedConnection>().Connection, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", GoalsDbContext.Schema);
        }).AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));
        services.AddScoped<ITransactionParticipant, DbContextParticipant<GoalsDbContext>>();
        services.AddScoped<IGoalStore, GoalStore>();
        services.AddScoped<IItemDeletionPart, GoalsDeletionPart>();
        services.AddScoped<GoalReader>();
        services.AddScoped<Nid.Goals.Contracts.IEmergencyBuffer, GoalsEmergencyBuffer>();
        services.AddScoped<IGoalsReview, GoalsReview>();
        services.AddScoped<ISeriesContribution, GoalSeries>();

        services.AddDbContext<Nid.Ai.Infrastructure.Persistence.AiDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<SharedConnection>().Connection, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", Nid.Ai.Infrastructure.Persistence.AiDbContext.Schema);
        }).AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));
        services.AddScoped<ITransactionParticipant, DbContextParticipant<Nid.Ai.Infrastructure.Persistence.AiDbContext>>();
        services.AddScoped<Nid.Ai.Application.INidKeys, Nid.Ai.Infrastructure.Persistence.NidKeyStore>();
        services.AddScoped<Nid.Ai.Application.IWorkerStore, Nid.Ai.Infrastructure.Persistence.WorkerStore>();
        services.AddScoped<Nid.Ai.Application.IJobStore, Nid.Ai.Infrastructure.Persistence.JobStore>();
        services.AddScoped<Nid.Ai.Application.IAiSettings, Nid.Ai.Infrastructure.Persistence.AiSettingsStore>();
        services.AddSingleton<Nid.Ai.Application.IModelContract, Nid.Ai.Application.EchoJobContract>();
        services.AddSingleton<Nid.Ai.Application.IModelContract, Nid.Ai.Application.EchoRequestContract>();
        services.AddSingleton<Nid.Ai.Application.IModelContract, Nid.Ai.Application.ChatRequestContract>();
        services.AddSingleton<Nid.Ai.Application.ModelContracts>();
        services.AddScoped<Nid.Ai.Contracts.IModelJobs, Nid.Ai.Application.ModelJobs>();
        services.AddScoped<Nid.Ai.Application.IWorkerRouter, Nid.Ai.Application.WorkerRouter>();
        services.AddScoped<Nid.Ai.Application.WorkerRefresher>();
        services.AddScoped<Nid.Ai.Application.IWorkerMonitor, Nid.Ai.Application.WorkerMonitor>();
        services.AddScoped<Nid.Ai.Application.IJobDispatcher, Nid.Ai.Application.JobDispatcher>();
        services.AddScoped<Nid.Ai.Application.IJobPurge, Nid.Ai.Application.JobPurge>();
        services.AddScoped<Nid.Ai.Application.IConversations, Nid.Ai.Infrastructure.Persistence.ConversationStore>();
        services.AddScoped<Nid.Ai.Application.IAssistantPeople, Nid.Ai.Infrastructure.Persistence.AssistantPeopleStore>();
        services.AddScoped<Nid.Ai.Application.IConversationPurge, Nid.Ai.Application.ConversationPurge>();
        services.AddScoped<Nid.Ai.Application.IModelRequests, Nid.Ai.Application.ModelRequests>();
        // #486: the one way to a model; workers are the first link of the chain.
        services.AddScoped<Nid.Ai.Application.ILlmCalls, Nid.Ai.Infrastructure.Persistence.LlmCallStore>();
        services.AddScoped<Nid.Ai.Application.ILlmCallReads, Nid.Ai.Infrastructure.Persistence.LlmCallReads>();
        services.AddScoped<Nid.Ai.Application.ILlmProvider, Nid.Ai.Application.WorkerProvider>();
        services.AddScoped<Nid.Ai.Application.ILlmGateway, Nid.Ai.Application.LlmGateway>();
        // #487: the cloud profiles Nid ships, their keys and settings, after the workers in the chain. The client reaches
        // nothing but the loaded profiles' hosts, on Myelin's address checks.
        services.TryAddSingleton<Nid.Ai.Application.ILlmProfiles, Nid.Ai.Infrastructure.Cloud.LlmProfileLoader>();
        services.AddScoped<Nid.Ai.Application.ICloudSettings, Nid.Ai.Infrastructure.Persistence.CloudSettingsStore>();
        services.AddSingleton<Nid.Ai.Application.CloudBreaker>();
        // #488: the household's monthly budget for paid providers.
        services.AddScoped<Nid.Ai.Application.IBudget, Nid.Ai.Infrastructure.Persistence.BudgetStore>();
        services.AddScoped<Nid.Ai.Application.BudgetPrices>();
        services.AddScoped<Nid.Ai.Application.BudgetNotices>();
        services.AddScoped<Nid.Ai.Application.IBudgetSweep, Nid.Ai.Application.BudgetSweep>();
        services.TryAddSingleton(sp =>
        {
            var hosts = sp.GetRequiredService<Nid.Ai.Application.ILlmProfiles>().All.Select(p => p.Host);
            var handler = UnambitiousFx.Myelin.MyelinHandler.Create(null);
            handler.ConnectTimeout = TimeSpan.FromSeconds(10);
            var http = new HttpClient(Nid.Ai.Infrastructure.Cloud.CloudHttp.Handler(hosts, handler)) { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(NidOutbound.UserAgent);
            return new Nid.Ai.Infrastructure.Cloud.CloudHttp(http);
        });
        services.AddSingleton<Nid.Ai.Application.ICloudAdapter, Nid.Ai.Infrastructure.Cloud.AnthropicAdapter>();
        services.AddSingleton<Nid.Ai.Application.ICloudAdapter, Nid.Ai.Infrastructure.Cloud.OpenAiAdapter>();
        services.AddSingleton<Nid.Ai.Application.ICloudAdapter, Nid.Ai.Infrastructure.Cloud.MistralAdapter>();
        services.AddSingleton<Nid.Ai.Application.ICloudAdapter, Nid.Ai.Infrastructure.Cloud.GeminiAdapter>();
        services.AddScoped<Nid.Ai.Application.ILlmProvider, Nid.Ai.Application.CloudProvider>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<Nid.Ai.Infrastructure.Workers.WorkerDirectory>();
        services.AddSingleton<UnambitiousFx.Myelin.IRegisteredEndpoints>(sp => sp.GetRequiredService<Nid.Ai.Infrastructure.Workers.WorkerDirectory>());
        services.AddSingleton<Nid.Ai.Application.IWorkerDirectory>(sp => sp.GetRequiredService<Nid.Ai.Infrastructure.Workers.WorkerDirectory>());
        // Workers are reached on the household's network: Myelin's handler lets exactly the registered host and port pairs
        // resolve to private addresses, and the TLS check is the directory's. One client for the host's life, as
        // SafeHttpClient: an IHttpClientFactory client keeps every disposed host alive (HostLifetimeTests).
        services.AddSingleton<Nid.Ai.Application.IWorkerClient>(sp =>
        {
            var directory = sp.GetRequiredService<Nid.Ai.Infrastructure.Workers.WorkerDirectory>();
            var handler = UnambitiousFx.Myelin.MyelinHandler.Create(null, directory);
            handler.ConnectTimeout = TimeSpan.FromSeconds(5); // a worker that is off: known in 5 s, not at the call's end
            handler.SslOptions.RemoteCertificateValidationCallback = (sender, certificate, _, errors)
                => directory.ValidateCertificate(((System.Net.Security.SslStream)sender).TargetHostName, certificate, errors);
            // Each call sets its own deadline (WorkerClient); a streamed request may last 10 minutes.
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(NidOutbound.UserAgent);
            return new Nid.Ai.Infrastructure.Workers.WorkerClient(http);
        });

        services.AddDbContext<BankingDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<SharedConnection>().Connection, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", BankingDbContext.Schema);
        }).AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));
        services.AddScoped<ITransactionParticipant, DbContextParticipant<BankingDbContext>>();
        services.AddScoped<IItemDeletionPart, Nid.Banking.Infrastructure.Persistence.BankingDeletionPart>();
        services.AddScoped<IApplicationStore, ApplicationStore>();
        services.AddScoped<IConnectionStore, ConnectionStore>();
        services.AddScoped<IAccountStore, AccountStore>();
        services.AddScoped<IBalanceStore, BalanceStore>();
        services.AddScoped<ITransactionStore, TransactionStore>();
        services.AddScoped<ILinkStore, LinkStore>();
        services.AddScoped<IRuleStore, RuleStore>();
        services.AddScoped<ICategoryStore, CategoryStore>();
        services.AddScoped<ISplitStore, SplitStore>();
        services.AddScoped<ICategoryRuleStore, CategoryRuleStore>();
        services.AddScoped<ICategoryBudgetStore, CategoryBudgetStore>();
        services.AddScoped<ICategorySuggestionStore, CategorySuggestionStore>();
        services.AddScoped<IBankSyncRunLog, SyncRunLog>();
        services.AddScoped<IAccountReadLog, AccountReadLog>();
        services.AddScoped<BankingAccess>();
        services.AddScoped<Nid.Banking.Application.Reconciliation.ExpectedChargeReader>();
        services.AddScoped<Nid.Banking.Application.Reconciliation.LinkCategoryReader>();
        services.AddScoped<Nid.Banking.Application.Reconciliation.TransactionLinker>();
        services.AddScoped<Nid.Banking.Application.Reconciliation.CategoryBackfill>();
        services.AddScoped<Nid.Banking.Application.Reconciliation.CategoryRuleApplier>();
        services.AddScoped<Nid.Banking.Application.Reconciliation.LineRecategoriser>();
        services.AddScoped<Nid.Banking.Application.Reconciliation.BankReconciliation>();
        services.AddScoped<Nid.Banking.Application.Reconciliation.SavingsFlows>();
        services.AddScoped<Nid.Banking.Application.Reconciliation.SavingsFlowBackfill>();
        services.AddScoped<Nid.Banking.Application.Reconciliation.RecurringDebits>();
        services.AddScoped<Nid.Banking.Application.BankLines>();
        services.AddScoped<Nid.Banking.Application.Operations.ReceiptAnchors>();
        services.AddScoped<Nid.Banking.Application.CategoryRuleScope>();
        services.AddScoped<Nid.Banking.Application.Spending.SpendingAccounts>();
        services.AddScoped<Nid.Banking.Application.Spending.CommittedByCategory>();
        services.AddScoped<ISpendingContribution, Nid.Banking.Application.Spending.DashboardSpending>();
        services.AddScoped<IProposalHandler, Nid.Banking.Application.Reconciliation.LinkProposal>();
        services.AddScoped<IProposalHandler, Nid.Banking.Application.Reconciliation.SubscriptionProposal>();
        services.AddScoped<IProposalHandler, Nid.Banking.Application.Reconciliation.CategoriseProposal>();
        services.AddScoped<Nid.Platform.Contracts.INotificationSource, BankingNotificationSource>();
        services.AddScoped<BankAccountReader>();
        services.AddScoped<Nid.Banking.Application.Sync.BankSyncCollector>();
        services.AddSingleton<Nid.Banking.Application.Sync.BalanceValuation>();
        services.AddSingleton<AspspCache>();
        services.AddDbContext<ChildcareDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<SharedConnection>().Connection, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", ChildcareDbContext.Schema);
        }).AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));
        services.AddScoped<ITransactionParticipant, DbContextParticipant<ChildcareDbContext>>();
        services.AddScoped<Nid.Childcare.Application.IChildcareStore, ChildcareStore>();
        services.AddScoped<Nid.Childcare.Application.INannyStore, NannyStore>();
        services.AddScoped<IItemDeletionPart, ChildcareDeletionPart>();
        services.AddScoped<IPersonUsePart, Nid.Childcare.Infrastructure.Persistence.ChildcarePersonUsePart>();
        services.AddScoped<Nid.Childcare.Application.ChildcareReader>();
        services.AddScoped<Nid.Childcare.Application.StatementAssembly>();
        services.AddScoped<ISeriesContribution, Nid.Childcare.Application.ChildcareSeries>();
        services.AddScoped<ITaxContribution, Nid.Childcare.Application.ChildcareTax>();
        services.AddScoped<ICommitmentsContribution, Nid.Childcare.Application.ChildcareCommitments>();
        services.AddScoped<IExpectedCharges, Nid.Childcare.Application.ChildcareCharges>();
        services.AddScoped<IImportSection, Nid.Childcare.Application.Import.ChildcareImportSection>();
        services.AddScoped<IImportSection, Nid.Childcare.Application.Import.NannyImportSection>();
        services.AddScoped<Nid.Platform.Contracts.INotificationSource, Nid.Childcare.Application.ChildcareNotificationSource>();

        services.AddDbContext<DocumentsDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<SharedConnection>().Connection, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", DocumentsDbContext.Schema);
        }).AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));
        services.AddScoped<ITransactionParticipant, DbContextParticipant<DocumentsDbContext>>();
        // After Banking's part (registered above): it re-anchors its lines' receipts off a deleted envelope before these links are removed.
        services.AddScoped<IItemDeletionPart, Nid.Documents.Infrastructure.Persistence.DocumentsDeletionPart>();
        services.AddScoped<IPersonUsePart, Nid.Documents.Infrastructure.Persistence.DocumentsPersonUsePart>();
        services.AddScoped<IDocumentStore, DocumentStore>();
        services.AddScoped<Nid.Documents.Contracts.IDocumentLinks, DocumentLinks>();
        services.AddScoped<Nid.Documents.Contracts.IDocumentTargets, CoreDocumentTargets>();
        services.AddScoped<Nid.Documents.Contracts.IDocumentTargets, Nid.Income.Application.IncomeDocumentTargets>();
        services.AddScoped<Nid.Documents.Contracts.IDocumentTargets, Nid.Banking.Application.Operations.BankingDocumentTargets>();
        services.AddScoped<DocumentAccess>();
        services.AddScoped<ExpectedDocuments>();

        services.AddDbContext<RealEstateDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<SharedConnection>().Connection, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", RealEstateDbContext.Schema);
        }).AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));
        services.AddScoped<ITransactionParticipant, DbContextParticipant<RealEstateDbContext>>();
        services.AddScoped<ILoanStore, LoanStore>();
        services.AddScoped<IPropertyStore, PropertyStore>();
        services.AddScoped<IItemDeletionPart, RealEstateDeletionPart>();
        services.AddScoped<Nid.RealEstate.Domain.IIrlStore, IrlStore>();
        services.AddScoped<ISeriesContribution, Nid.RealEstate.Application.Irl.IrlSeries>();
        services.AddScoped<ISeriesContribution, Nid.RealEstate.Application.LoanSeries>();
        services.AddScoped<ISeriesContribution, Nid.RealEstate.Application.PropertySeries>();
        services.AddScoped<ISeriesContribution, Nid.RealEstate.Application.TenancySeries>();
        services.AddScoped<IIrlRunLog, IrlRunLog>();
        services.AddScoped<Nid.Platform.Contracts.ISyncHealth, Nid.RealEstate.Infrastructure.Persistence.IrlSyncHealth>();
        services.AddScoped<Nid.Core.Domain.Inflation.ICpiStore, Nid.Core.Infrastructure.Persistence.CpiStore>();
        services.AddScoped<Nid.Core.Contracts.ICpiRunLog, Nid.Core.Infrastructure.Persistence.CpiRunLog>();
        services.AddScoped<IInflation, ObservedInflation>();
        services.AddScoped<ISeriesContribution, Nid.Core.Application.Series.CpiSeries>();
        services.AddScoped<ISeriesContribution, Nid.Core.Application.Series.ProjectionRateSeries>();
        services.AddScoped<ISeriesContribution, Nid.Core.Application.Series.NetWorthSeries>();
        services.AddScoped<ISeriesContribution, Nid.Core.Application.Series.TaxSeries>();
        services.AddScoped<CpiCollector>();
        services.AddScoped<Nid.Platform.Contracts.ISyncHealth, Nid.Core.Infrastructure.Persistence.CpiSyncHealth>();
        services.AddScoped<Nid.RealEstate.Application.Irl.IrlCollector>();
        services.AddScoped<IRealEstateImports, RealEstateImports>();
        services.AddScoped<ILoanSimulator, Nid.RealEstate.Application.ScenarioLoans>();
        services.AddScoped<IRealEstateProjection, Nid.RealEstate.Application.RealEstateScenarioProjection>();
        services.AddScoped<Nid.RealEstate.Application.PropertyReading>();
        services.AddScoped<INetWorthContribution, Nid.RealEstate.Application.RealEstateNetWorth>();
        services.AddScoped<ILoansReview, Nid.RealEstate.Application.LoansReview>();
        services.AddScoped<IPropertiesReview, Nid.RealEstate.Application.PropertiesReview>();
        services.AddScoped<ICommitmentsContribution, Nid.RealEstate.Application.LoanCommitments>();
        services.AddScoped<ICommitmentsContribution, Nid.RealEstate.Application.PropertyCostCommitments>();
        services.AddScoped<IExpectedCharges, Nid.RealEstate.Application.RealEstateExpectedCharges>();
        services.AddScoped<Nid.RealEstate.Application.RealEstateUpcoming>();
        services.AddScoped<IUpcomingEvents>(sp => sp.GetRequiredService<Nid.RealEstate.Application.RealEstateUpcoming>());
        services.AddScoped<Nid.RealEstate.Application.PropertiesOverviewReader>();
        services.AddScoped<Nid.Platform.Contracts.INotificationSource, Nid.RealEstate.Application.RealEstateNotificationSource>();
        services.AddScoped<ITaxContribution, Nid.RealEstate.Application.RentalTax>();
        services.AddScoped<ISaleContribution, Nid.RealEstate.Application.PropertiesSale>();
        services.AddScoped<Nid.RealEstate.Application.RealEstateRents>();
        services.AddScoped<IRentIncome>(sp => sp.GetRequiredService<Nid.RealEstate.Application.RealEstateRents>());
        services.AddScoped<IIncomeContribution>(sp => sp.GetRequiredService<Nid.RealEstate.Application.RealEstateRents>());
        services.AddScoped<IPropertyIncome, Nid.RealEstate.Application.RealEstatePropertyIncome>();
        services.AddScoped<ILoanFinancing, Nid.RealEstate.Application.LoanFinancing>();
        services.AddScoped<IProposalHandler, Nid.RealEstate.Application.AmortizationProposal>();
        services.AddSingleton<IFactConventions, Nid.RealEstate.Application.RealEstateFactConventions>();
        services.AddSingleton<IFactConventions, Nid.Investments.Application.InvestmentsFactConventions>();

        // Assets (1C2c): files named by their hash, on a volume. Program binds the options; the default is the
        // production volume. Nothing touches the disk before the first write, so building the host never does.
        services.TryAddSingleton(new AssetOptions());
        services.TryAddSingleton<IAssetStore, FileAssetStore>();

        // Provider catalog (1C2b). The options are bound by Program; the defaults are the design's pinned commit.
        services.TryAddSingleton(new CatalogOptions());
        services.TryAddSingleton<ICatalogSourceClient, CatalogSourceClient>();
        services.TryAddSingleton<ICatalogJobTrigger, JobsDisabledCatalogTrigger>();
        // Icons (1C2c): downloaded through the SafeHttpClient, processed, stored; the pass runs inside each sync.
        services.TryAddSingleton<ICatalogIconSource>(sp => new CatalogIconSource(sp.GetRequiredService<SafeHttpClient>()));
        services.AddScoped<CatalogIconPass>();
        services.AddScoped<IProviderStore, ProviderStore>();
        services.AddScoped<ICatalogRunLog, CatalogRunLog>();
        services.AddScoped<Nid.Platform.Contracts.ISyncHealth, Nid.Subscriptions.Infrastructure.Persistence.CatalogSyncHealth>();
        services.AddScoped<CatalogSync>();
        services.TryAddSingleton<IProviderKeySuffix, RandomProviderKeySuffix>();
        services.AddScoped<ProviderAccess>();
        services.AddScoped<ProviderKeys>();

        services.AddDbContext<PlatformDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<SharedConnection>().Connection, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", PlatformDbContext.Schema);
        }).AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));
        services.AddScoped<ITransactionParticipant, DbContextParticipant<PlatformDbContext>>();
        services.AddScoped<IImportStaging, ImportStagingStore>();
        services.AddScoped<Nid.Platform.Notifications.INotificationStore, Nid.Platform.Infrastructure.Notifications.NotificationStore>();
        services.AddScoped<Nid.Platform.Notifications.IWebhookStore, Nid.Platform.Infrastructure.Notifications.WebhookStore>();
        services.AddScoped<Nid.Platform.Admin.IJobRunStore, Nid.Platform.Infrastructure.Admin.JobRunStore>();
        // Scoped: the sources depend on scoped stores, a singleton catalog would capture them.
        services.AddScoped<Nid.Platform.Notifications.NotificationCatalog>();
        services.AddScoped<ISignalSettings, Nid.Platform.Notifications.SignalSettings>();
        services.AddScoped<Nid.Platform.Contracts.INotificationSource, Nid.Platform.Notifications.SystemHealthNotificationSource>();
        services.TryAddSingleton<INotificationRefresh, NoNotificationRefresh>();
        services.TryAddSingleton<IEmailAvailability, NoEmailAvailability>();
        services.AddScoped<IDeliveryStore, Nid.Platform.Infrastructure.Notifications.DeliveryStore>();
        services.AddScoped<IDigestStore, Nid.Platform.Infrastructure.Notifications.DeliveryStore>();
        services.AddScoped<IDeliveryOutbox, DeliveryOutbox>();
        services.AddSingleton<INotifier, Nid.Platform.Infrastructure.Notifications.Notifier>();
        // The wording of the outside; the link base and the email availability come from AddNidEmail (Program).
        services.TryAddSingleton(new NotificationLinkBase(""));
        services.TryAddSingleton<INotificationRenderer, ExternalNotificationRenderer>();
        services.AddSingleton(new ImportOptions());
        services.AddScoped<ImportRunner>();
        services.AddScoped<ExportBuilder>();
        services.AddScoped<IImportSection, PersonsImportSection>();
        services.AddScoped<IImportSection, SubscriptionsImportSection>();
        services.AddScoped<IImportSection, InsuranceImportSection>();
        services.AddScoped<IImportSection, EnvelopesImportSection>();
        services.AddScoped<IImportSection, HoldingsImportSection>();
        services.AddScoped<IImportSection, BreakdownsImportSection>();
        services.AddScoped<IImportSection, IncomeImportSection>();
        services.AddScoped<IImportSection, IncomeOneOffTaxableImportSection>();
        services.AddScoped<IImportSection, Nid.Goals.Application.Import.GoalsImportSection>();
        services.AddScoped<IImportSection, Nid.RealEstate.Application.Import.RealEstateImportSection>();
        services.AddScoped<IImportSection, FactsImportSection>();
        services.AddScoped<IImportSection, Nid.Banking.Application.Import.BankImportSection>();

        services.AddLogging();
        services.AddNidAuthorization();
        services.AddSynapse(cfg =>
        {
            cfg.AddRegisterGroup(new RegisterGroup());                    // this assembly: carries the global behaviors
            cfg.AddRegisterGroup(new Nid.Platform.RegisterGroup());
            cfg.AddRegisterGroup(new Nid.Core.Application.RegisterGroup());
            cfg.AddRegisterGroup(new Nid.Subscriptions.Application.RegisterGroup());
            cfg.AddRegisterGroup(new Nid.Investments.Application.RegisterGroup());
            cfg.AddRegisterGroup(new Nid.Income.Application.RegisterGroup());
            cfg.AddRegisterGroup(new Nid.Goals.Application.RegisterGroup());
            cfg.AddRegisterGroup(new Nid.Ai.Application.RegisterGroup());
            cfg.AddRegisterGroup(new Nid.Childcare.Application.RegisterGroup());
            cfg.AddRegisterGroup(new Nid.Documents.Application.RegisterGroup());
            cfg.AddRegisterGroup(new Nid.RealEstate.Application.RegisterGroup());
            cfg.AddRegisterGroup(new Nid.Banking.Application.RegisterGroup());
            cfg.AddTransactions<UnitOfWorkTransactions>();                 // an ITransactional command runs in the request's unit of work
            cfg.ValidateOnStart();                                         // a handler or behavior that cannot resolve fails the start, not the first request
        });
        return services;
    }
}
