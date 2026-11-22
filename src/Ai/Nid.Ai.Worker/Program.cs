using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NSign.AspNetCore;
using Nid.Ai.Protocol.Signing;
using Nid.Ai.Worker;

if (args.Length > 0 && Cli.Commands.Contains(args[0]))
    return Cli.Execute(args);

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton(sp => new NonceCache(sp.GetRequiredService<TimeProvider>()));
builder.Services.AddHttpContextAccessor();
builder.Services.AddContentDigestVerification();
builder.Services.AddSignatureVerification(sp =>
{
    var store = sp.GetRequiredService<StateStore>();
    var context = sp.GetRequiredService<IHttpContextAccessor>();
    return WorkerSignatures.Verifier(id => WorkerEndpoints.NidKeyById(context.HttpContext, store, id), sp.GetRequiredService<NonceCache>());
});
builder.Services.AddOptions<RequestSignatureVerificationOptions>()
    .Configure<TimeProvider>((options, time) => WorkerSignatures.ConfigureVerification(options, time));

builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    var worker = context.Configuration.GetSection("Worker").Get<WorkerOptions>() ?? new WorkerOptions();
    kestrel.AddServerHeader = false;
    var certificate = new Lazy<System.Security.Cryptography.X509Certificates.X509Certificate2>(() =>
        SelfSignedCertificate.Load(kestrel.ApplicationServices.GetRequiredService<StateStore>().Load().TlsCertificate));
    kestrel.ListenAnyIP(worker.Port, listen => listen.UseHttps(https => https.ServerCertificateSelector = (_, _) => certificate.Value));
});

var app = builder.Build();

var state = app.Services.GetRequiredService<StateStore>().Start();
app.Logger.LogInformation("{Status}", StateStore.Describe(state));

app.UseWhen(context => context.Request.Path == WorkerEndpoints.PairPath, pairing =>
{
    pairing.Use(WorkerEndpoints.OpenPairing);
    pairing.UseContentDigestVerification();
    pairing.UseSignatureVerification();
});
app.UseWhen(context => WorkerEndpoints.IsSigned(context.Request.Path), signed =>
{
    signed.UseContentDigestVerification();
    signed.UseSignatureVerification();
});
WorkerEndpoints.Map(app);

await app.RunAsync();
return 0;

/// <summary>The entry point, visible to the in-memory test host.</summary>
public partial class Program;
