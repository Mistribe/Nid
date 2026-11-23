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
builder.Services.AddSingleton<IJobHandler, EchoJob>();
builder.Services.AddSingleton<IJobHandler, DocumentTextJob>();
builder.Services.AddSingleton<IRequestHandler, EchoRequest>();
builder.Services.Configure<EngineOptions>(builder.Configuration.GetSection("Engine"));
builder.Services.TryAddSingleton(new EngineTransport(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) }));
builder.Services.AddSingleton<EngineClient>();
builder.Services.AddSingleton<IRequestHandler, ChatRequest>();
builder.Services.AddSingleton<JobTable>();
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
    // 2 MiB for every route; /v1/jobs raises it for itself (a job's file), before anything reads a body.
    kestrel.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
    var certificate = new Lazy<System.Security.Cryptography.X509Certificates.X509Certificate2>(() =>
        SelfSignedCertificate.Load(kestrel.ApplicationServices.GetRequiredService<StateStore>().Load().TlsCertificate));
    kestrel.ListenAnyIP(worker.Port, listen => listen.UseHttps(https => https.ServerCertificateSelector = (_, _) => certificate.Value));
});

var app = builder.Build();

var state = app.Services.GetRequiredService<StateStore>().Start();
app.Logger.LogInformation("{Status}", StateStore.Describe(state));

app.Use((context, next) =>
{
    if (context.Request.Path == "/v1/jobs"
        && context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        limit.MaxRequestBodySize = WorkerEndpoints.MaxJobBytes;
    return next(context);
});
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
