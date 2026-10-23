using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Host;
using Nid.Host.Admin;
using Nid.Host.Api;
using Nid.Host.Auth;
using Nid.Host.Jobs;
using Nid.Host.Mcp;
using Nid.Goals.Infrastructure.Persistence;
using Nid.Income.Infrastructure.Persistence;
using Nid.Investments.Contracts;
using Nid.Investments.Infrastructure.Persistence;
using Nid.Platform;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Persistence;
using Nid.Platform.Outbound;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Infrastructure.Persistence;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Infrastructure.Persistence;
using UnambitiousFx.Synapse.Abstractions;

// Shared behaviors are declared here, in the composition root: [PipelineBehavior] only reaches
// handlers in assemblies the declaring assembly references, and modules reference Platform, not the reverse.
[assembly: SynapseGlobalBehavior(typeof(TraceBehavior<,>))]
[assembly: SynapseGlobalBehavior(typeof(ActorBehavior<,>))]
[assembly: SynapseGlobalBehavior(typeof(Nid.Platform.Audit.AuditBehavior<,>))]
[assembly: SynapseGlobalBehavior(typeof(AdminBehavior<,>))]
[assembly: SynapseGlobalBehavior(typeof(ScopeBehavior<,>))]
[assembly: SynapseGlobalBehavior(typeof(ItemAccessBehavior<,>))]
[assembly: SynapseGlobalBehavior(typeof(OutputGuardBehavior<,>))]

// 1F4: `nid admin …` runs one use case and stops (AdminCli).
var adminMode = args is ["admin", ..];
var builder = WebApplication.CreateBuilder(args);
// The CLI's output is for the person at the terminal, not EF's SQL.
if (adminMode) builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Configuration.AddSecretFiles();
builder.AddServiceDefaults();
// Build-time OpenAPI generation (Microsoft.Extensions.ApiDescription.Server) runs this entry point up to
// Build() with no configuration: it must not throw for a missing connection string, and nothing may connect
// (the data source is built lazily, on first use). The entry point keeps running after Build() while the
// generator reads the document, so the migrations below are skipped then — and so are the scheduled jobs,
// whose hosted services would otherwise start against the placeholder connection string.
var generatingOpenApi = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
var fxOptions = builder.Configuration.GetSection("Fx").Get<FxOptions>() ?? new FxOptions();
builder.Services.AddSingleton(fxOptions);
var priceOptions = builder.Configuration.GetSection("Prices").Get<PriceOptions>() ?? new PriceOptions();
builder.Services.AddSingleton(priceOptions);
var irlOptions = builder.Configuration.GetSection("Irl").Get<IrlOptions>() ?? new IrlOptions();
builder.Services.AddSingleton(irlOptions);
builder.Services.AddSingleton(builder.Configuration.GetSection("Outbound").Get<OutboundOptions>() ?? new OutboundOptions());
builder.Services.AddSingleton(builder.Configuration.GetSection("Catalog").Get<CatalogOptions>() ?? new CatalogOptions());
// Parsed here so a bad setting stops the host at startup (spec 4D1 D6).
builder.Services.AddSingleton(Nid.Income.Application.PayslipTolerance.Parse(builder.Configuration["Income:PayslipTolerance"]));
builder.Services.AddSingleton(builder.Configuration.GetSection("Assets").Get<AssetOptions>() ?? new AssetOptions());
var connectionString = builder.Configuration.GetConnectionString("nid")
    ?? (generatingOpenApi ? "Host=openapi-generation" : throw new InvalidOperationException("ConnectionStrings:nid is not configured."));
builder.Services.AddNid(connectionString);
builder.Services.AddNidOpenApi();
if (!generatingOpenApi && !adminMode && builder.Configuration.GetValue("Jobs:Enabled", true))
    builder.Services.AddNidJobs(connectionString, fxOptions, priceOptions, irlOptions, builder.Configuration.GetValue("Jobs:SchedulerName", "nid")!);

builder.Services.AddNidAuthentication(builder.Configuration);
builder.Services.AddTokenRateLimiter();
builder.Services.AddNidMcp();
builder.Services.AddNidDataProtection(builder.Configuration);
builder.Services.AddNidHealth();
// Never set in production: swaps the default scheme so IntegrationTestAuthenticationHandler — which reads
// IntegrationTestGate.ActorHeader itself — becomes what authenticates every request.
if (builder.Environment.IsEnvironment(IntegrationTestGate.Environment))
    builder.Services.AddIntegrationTestAuthentication();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new ItemIdJsonConverter());
    o.SerializerOptions.Converters.Add(new LocalDateJsonConverter());
    o.SerializerOptions.Converters.Add(new OneOrManyJsonConverter<InvestmentEndpoints.SwitchLegBody>());
    // The web defaults accept "5" for an integer, and the document then types every integer as
    // integer | string. A number sent as a string is a 400 like any other unreadable body.
    o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});

var app = builder.Build();

// 1F4: `nid admin …` runs one use case and stops; it never migrates and never serves.
if (adminMode)
{
    var exit = await AdminCli.RunAsync(app.Services, args[1..],
        new CliConsole(Console.In, Console.Out, Console.Error, !Console.IsInputRedirected));
    // The console logger writes on a background queue: disposing the host flushes it before the process ends.
    await app.DisposeAsync();
    return exit;
}

// EF Core takes a database lock while migrating, so several instances starting together are safe.
// Health:SkipMigrations is for the readiness test only (HostingTests): it starts a host whose database is unreachable.
if (!generatingOpenApi && !builder.Configuration.GetValue<bool>("Health:SkipMigrations"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<CoreDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<SubscriptionsDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<InvestmentsDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<IncomeDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<GoalsDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<RealEstateDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.MigrateAsync();
}

app.MapDefaultEndpoints();
app.MapNidHealth();
app.UseForwardedHeaders();
app.UseMiddleware<TokenFailureGateMiddleware>();
app.UseAuthentication();
app.UseMiddleware<TokenRateLimitMiddleware>();
app.UseMiddleware<CsrfMiddleware>();
app.UseAuthorization();
app.MapAuth();
app.MapPersonEndpoints();
app.MapProposalEndpoints();
app.MapSubscriptionEndpoints();
app.MapInvestmentEndpoints();
app.MapIncomeEndpoints();
app.MapGoalEndpoints();
app.MapRealEstateEndpoints();
app.MapDashboardEndpoints();
app.MapTaxEndpoints();
app.MapScenarioEndpoints();
app.MapImportEndpoints();
app.MapExportEndpoints();
app.MapPlatformEndpoints();
app.MapFxEndpoints();
app.MapCatalogEndpoints();
app.MapAdminEndpoints();
app.MapNidMcp();
app.Run();
return 0;

public partial class Program;
