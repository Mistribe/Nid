using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Host;
using Nid.Host.Api;
using Nid.Host.Auth;
using Nid.Host.Jobs;
using Nid.Platform;
using Nid.Platform.Infrastructure.Persistence;
using Nid.Platform.Outbound;
using Nid.Subscriptions.Infrastructure.Persistence;
using UnambitiousFx.Synapse.Abstractions;

// Shared behaviors are declared here, in the composition root: [PipelineBehavior] only reaches
// handlers in assemblies the declaring assembly references, and modules reference Platform, not the reverse.
[assembly: SynapseGlobalBehavior(typeof(TraceBehavior<,>))]
[assembly: SynapseGlobalBehavior(typeof(ActorBehavior<,>))]
[assembly: SynapseGlobalBehavior(typeof(ScopeBehavior<,>))]
[assembly: SynapseGlobalBehavior(typeof(ItemAccessBehavior<,>))]
[assembly: SynapseGlobalBehavior(typeof(OutputGuardBehavior<,>))]

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var fxOptions = builder.Configuration.GetSection("Fx").Get<FxOptions>() ?? new FxOptions();
builder.Services.AddSingleton(fxOptions);
builder.Services.AddSingleton(builder.Configuration.GetSection("Outbound").Get<OutboundOptions>() ?? new OutboundOptions());
var connectionString = builder.Configuration.GetConnectionString("nid")
                       ?? throw new InvalidOperationException("ConnectionStrings:nid is not configured.");
builder.Services.AddNid(connectionString);
if (builder.Configuration.GetValue("Jobs:Enabled", true))
    builder.Services.AddNidJobs(connectionString, fxOptions, builder.Configuration.GetValue("Jobs:SchedulerName", "nid")!);

builder.Services.AddNidAuthentication(builder.Configuration);
// Never set in production: swaps the default scheme so IntegrationTestAuthenticationHandler — which reads
// IntegrationTestGate.ActorHeader itself — becomes what authenticates every request.
if (builder.Environment.IsEnvironment(IntegrationTestGate.Environment))
    builder.Services.AddIntegrationTestAuthentication();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new ItemIdJsonConverter());
    o.SerializerOptions.Converters.Add(new LocalDateJsonConverter());
});

var app = builder.Build();

// EF Core takes a database lock while migrating, so several instances starting together are safe.
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<CoreDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<SubscriptionsDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
}

app.MapDefaultEndpoints();
app.UseForwardedHeaders();
app.UseAuthentication();
app.UseMiddleware<CsrfMiddleware>();
app.UseAuthorization();
app.MapAuth();
app.MapPersonEndpoints();
app.MapSubscriptionEndpoints();
app.MapImportEndpoints();
app.Run();

public partial class Program;
