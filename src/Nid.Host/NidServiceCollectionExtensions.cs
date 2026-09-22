using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Synapse;

namespace Nid.Host;

public static class NidServiceCollectionExtensions
{
    public static IServiceCollection AddNid(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddDbContext<CoreDbContext>(options => options.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", CoreDbContext.Schema);
        }));
        services.AddScoped<IPersonRegistry, PersonRegistry>();
        services.AddScoped<IItemRegistry, ItemRegistry>();
        services.AddScoped<IAccessPolicy, AccessPolicy>();
        services.AddScoped<IActorAccessor, ActorAccessor>();

        services.AddLogging();
        services.AddSynapse(cfg =>
        {
            cfg.AddRegisterGroup(new RegisterGroup());                    // this assembly: carries the global behaviors
            cfg.AddRegisterGroup(new Nid.Core.Application.RegisterGroup());
        });
        return services;
    }
}
