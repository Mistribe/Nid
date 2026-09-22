using Microsoft.EntityFrameworkCore;
using Nid.Core.Infrastructure.Persistence;
using Nid.Host;
using Nid.Host.Api;
using Nid.Host.Auth;
using Nid.Platform;
using UnambitiousFx.Synapse.Abstractions;

// Shared behaviors are declared here, in the composition root: [PipelineBehavior] only reaches
// handlers in assemblies the declaring assembly references, and modules reference Platform, not the reverse.
[assembly: SynapseGlobalBehavior(typeof(TraceBehavior<,>))]
[assembly: SynapseGlobalBehavior(typeof(ActorBehavior<,>))]
[assembly: SynapseGlobalBehavior(typeof(ScopeBehavior<,>))]
[assembly: SynapseGlobalBehavior(typeof(ItemAccessBehavior<,>))]

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddNid(builder.Configuration.GetConnectionString("nid")
                         ?? throw new InvalidOperationException("ConnectionStrings:nid is not configured."));

builder.Services.AddNidAuthentication(builder.Configuration);

var app = builder.Build();

// EF Core takes a database lock while migrating, so several instances starting together are safe.
await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<CoreDbContext>().Database.MigrateAsync();

app.MapDefaultEndpoints();
app.UseForwardedHeaders();
app.UseAuthentication();
app.UseMiddleware<CsrfMiddleware>();
app.UseAuthorization();
app.MapAuth();
app.MapPersonEndpoints();
app.Run();

public partial class Program;
