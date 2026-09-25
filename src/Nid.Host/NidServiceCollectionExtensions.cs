using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Core.Application.Fx;
using Nid.Core.Application.Import;
using Nid.Core.Contracts;
using Nid.Core.Domain;
using Nid.Core.Infrastructure.Fx;
using Nid.Core.Infrastructure.Persistence;
using Nid.Host.Admin;
using Nid.Host.Jobs;
using Nid.Platform;
using Nid.Platform.Admin;
using Nid.Platform.Contracts;
using Nid.Platform.Import;
using Nid.Platform.Infrastructure.Assets;
using Nid.Platform.Infrastructure.Persistence;
using Nid.Platform.Outbound;
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
            dataSourceBuilder.UseNodaTime();
            return dataSourceBuilder.Build();
        });
        services.AddScoped<SharedConnection>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddDbContext<CoreDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<SharedConnection>().Connection, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", CoreDbContext.Schema);
        }));
        services.AddScoped<ITransactionParticipant, DbContextParticipant<CoreDbContext>>();
        services.AddScoped<IPersonRegistry, PersonRegistry>();
        services.AddScoped<IItemRegistry, ItemRegistry>();
        services.AddScoped<IAccessPolicy, AccessPolicy>();
        services.AddSingleton<IItemRules, ItemRules>();
        services.AddScoped<IVisibleItems, VisibleItems>();
        services.AddScoped<IActorAccessor, ActorAccessor>();
        services.AddSingleton<IAccessViolations, LoggingAccessViolations>();
        services.AddSingleton(new EnabledModules(["core", "subscriptions", "import"]));

        // Exchange rates (1C2a). The options are bound by Program from configuration before AddNid; the defaults
        // below only apply when nothing was bound (tests, tools).
        services.TryAddSingleton(new FxOptions());
        services.TryAddSingleton(new OutboundOptions());
        services.TryAddSingleton<IHostResolver, DnsHostResolver>();
        services.TryAddSingleton(sp => new SafeHttpClient(
            new HttpMessageInvoker(OutboundHandler.Create(sp.GetRequiredService<IHostResolver>())),
            sp.GetRequiredService<OutboundOptions>(),
            sp.GetRequiredService<ILogger<SafeHttpClient>>()));
        services.TryAddSingleton<IFxSourceClient, FxSourceClient>();
        services.TryAddSingleton<IFxJobTrigger, JobsDisabledFxTrigger>();
        services.AddScoped<IFxRateStore, FxRateStore>();
        services.AddScoped<IFxRunLog, FxRunLog>();
        services.AddScoped<IFxRates, FxRates>();
        services.AddScoped<IHouseholdSettings, HouseholdSettings>();
        services.AddScoped<FxCollector>();

        // Admin area (1G): the job list is Quartz's when AddNidJobs runs, empty otherwise.
        services.AddSingleton(new ProcessStart());
        services.TryAddSingleton<IJobStatusReader, NoJobStatusReader>();
        services.TryAddSingleton<IIdentityProviderProbe, OidcIdentityProviderProbe>();
        services.AddScoped<ISystemStatusSource, SystemStatusSource>();

        services.AddDbContext<SubscriptionsDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<SharedConnection>().Connection, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", SubscriptionsDbContext.Schema);
        }));
        services.AddScoped<ITransactionParticipant, DbContextParticipant<SubscriptionsDbContext>>();
        services.AddScoped<ISubscriptionStore, SubscriptionStore>();

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
        services.AddScoped<CatalogSync>();
        services.TryAddSingleton<IProviderKeySuffix, RandomProviderKeySuffix>();
        services.AddScoped<ProviderAccess>();
        services.AddScoped<ProviderKeys>();

        services.AddDbContext<PlatformDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<SharedConnection>().Connection, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", PlatformDbContext.Schema);
        }));
        services.AddScoped<ITransactionParticipant, DbContextParticipant<PlatformDbContext>>();
        services.AddScoped<IImportStaging, ImportStagingStore>();
        services.AddSingleton(new ImportOptions());
        services.AddScoped<ImportRunner>();
        services.AddScoped<IImportSection, PersonsImportSection>();
        services.AddScoped<IImportSection, SubscriptionsImportSection>();

        services.AddLogging();
        services.AddSynapse(cfg =>
        {
            cfg.AddRegisterGroup(new RegisterGroup());                    // this assembly: carries the global behaviors
            cfg.AddRegisterGroup(new Nid.Platform.RegisterGroup());
            cfg.AddRegisterGroup(new Nid.Core.Application.RegisterGroup());
            cfg.AddRegisterGroup(new Nid.Subscriptions.Application.RegisterGroup());
        });
        return services;
    }
}
