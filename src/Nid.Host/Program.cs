using System.Text.Json.Serialization;
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
// Build-time OpenAPI generation (Microsoft.Extensions.ApiDescription.Server) runs this entry point up to
// Build() with no configuration: it must not throw for a missing connection string, and nothing may connect
// (the data source is built lazily, on first use). The entry point keeps running after Build() while the
// generator reads the document, so the migrations below are skipped then — and so are the scheduled jobs,
// whose hosted services would otherwise start against the placeholder connection string.
var generatingOpenApi = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
var fxOptions = builder.Configuration.GetSection("Fx").Get<FxOptions>() ?? new FxOptions();
builder.Services.AddSingleton(fxOptions);
builder.Services.AddSingleton(builder.Configuration.GetSection("Outbound").Get<OutboundOptions>() ?? new OutboundOptions());
var connectionString = builder.Configuration.GetConnectionString("nid")
    ?? (generatingOpenApi ? "Host=openapi-generation" : throw new InvalidOperationException("ConnectionStrings:nid is not configured."));
builder.Services.AddNid(connectionString);
builder.Services.AddNidOpenApi();
if (!generatingOpenApi && builder.Configuration.GetValue("Jobs:Enabled", true))
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
    // The web defaults accept "5" for an integer, and the document then types every integer as
    // integer | string. A number sent as a string is a 400 like any other unreadable body.
    o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});

var app = builder.Build();

// EF Core takes a database lock while migrating, so several instances starting together are safe.
if (!generatingOpenApi)
{
    await using var scope = app.Services.CreateAsyncScope();
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
app.MapPlatformEndpoints();
app.MapFxEndpoints();
app.Run();

public partial class Program;
