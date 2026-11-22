extern alias worker;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Infrastructure.Workers;
using Nid.TestSupport;
using WorkerProgram = worker::Program;
using WorkerStateStore = worker::Nid.Ai.Worker.StateStore;

namespace Nid.Ai.IntegrationTests;

/// <summary>A real worker in memory, and Nid started with its worker client wired to it (Myelin and TLS are covered by
/// the unit tests of the directory and by the Compose check: a TestServer has neither DNS nor TLS).</summary>
public sealed class PairedWorld : IAsyncDisposable
{
    public const string WorkerUrl = "https://worker.test:8443";

    private PairedWorld(WorkerHost worker, NidApp nid)
    {
        Worker = worker;
        Nid = nid;
    }

    public WorkerHost Worker { get; }

    public NidApp Nid { get; }

    public static async Task<PairedWorld> StartAsync(PostgresFixture fixture, WorkerHost? worker = null)
    {
        worker ??= new WorkerHost();
        var nid = await NidApp.StartAsync(fixture, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IWorkerClient>(_ => new WorkerClient(new HttpClient(worker.Server.CreateHandler()))));
        });
        return new PairedWorld(worker, nid);
    }

    public Task<HttpResponseMessage> RegisterAsync(Guid? asPerson = null, string url = WorkerUrl)
        => Nid.RawAsync(asPerson is null ? Nid.AdultA : new Core.Contracts.PersonId(asPerson.Value), HttpMethod.Post, "/api/v1/ai/workers",
            new { name = "GPU box", url, placement = "local", proxyDeclared = false });

    public async Task<WorkerView> PairAsync()
    {
        var registered = await (await RegisterAsync()).Content.ReadFromJsonAsync<RegisteredWorker>(NidJson);
        var paired = await Nid.RawAsync(Nid.AdultA, HttpMethod.Post, $"/api/v1/ai/workers/{registered!.Id}/pair",
            new { fingerprint = registered.Fingerprint, code = Worker.PairingCode() });
        paired.EnsureSuccessStatusCode();
        return (await paired.Content.ReadFromJsonAsync<WorkerView>(NidJson))!;
    }

    public async Task<WorkerList> ListAsync(Core.Contracts.PersonId? asPerson = null)
    {
        var response = await Nid.RawAsync(asPerson ?? Nid.AdultA, HttpMethod.Get, "/api/v1/ai/workers");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WorkerList>(NidJson))!;
    }

    public static readonly System.Text.Json.JsonSerializerOptions NidJson = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new Nid.Host.Api.InstantJsonConverter() },
    };

    public async ValueTask DisposeAsync()
    {
        await Nid.DisposeAsync();
        await Worker.DisposeAsync();
    }
}

public sealed class WorkerHost : WebApplicationFactory<WorkerProgram>
{
    public string DataPath { get; } = Directory.CreateTempSubdirectory("nid-worker-").FullName;

    public TestClock Clock { get; } = new();

    public WorkerStateStore State => Services.GetRequiredService<WorkerStateStore>();

    public string PairingCode() => State.Load().PairingCode!;

    public int Run(params string[] command) => worker::Nid.Ai.Worker.Cli.Run(command, State, TextWriter.Null);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Worker:DataPath", DataPath);
        builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock)));
    }
}

public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => Now;
}
