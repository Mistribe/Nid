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
using NodaTime;
using WorkerProgram = worker::Program;
using WorkerStateStore = worker::Nid.Ai.Worker.StateStore;

namespace Nid.Ai.IntegrationTests;

/// <summary>Real workers in memory, and Nid started with its worker client wired to them by host (Myelin and TLS are
/// covered by the unit tests of the directory and by the Compose check: a TestServer has neither DNS nor TLS).</summary>
public sealed class PairedWorld : IAsyncDisposable
{
    public const string WorkerUrl = "https://worker.test:8443";

    private PairedWorld(IReadOnlyList<WorkerHost> workers, NidApp nid, RoutingHandler routes, MovableClock clock)
    {
        Workers = workers;
        Nid = nid;
        Routes = routes;
        Clock = clock;
    }

    public IReadOnlyList<WorkerHost> Workers { get; }

    public WorkerHost Worker => Workers[0];

    public NidApp Nid { get; }

    /// <summary>Which worker each host reaches; a host marked down fails like an unreachable one.</summary>
    public RoutingHandler Routes { get; }

    /// <summary>Nid's clock, moved by the tests (the host's fixed clock otherwise).</summary>
    public MovableClock Clock { get; }

    public static string UrlOf(int index) => index == 0 ? WorkerUrl : $"https://worker{index}.test:8443";

    public static async Task<PairedWorld> StartAsync(
        PostgresFixture fixture, WorkerHost? worker = null, int workers = 1, Action<IServiceCollection>? nidServices = null,
        Action<IServiceCollection>? workerServices = null)
    {
        var hosts = new List<WorkerHost>();
        for (var i = 0; i < workers; i++)
            hosts.Add(i == 0 && worker is not null ? worker : new WorkerHost(workerServices));
        var routes = new RoutingHandler(hosts.Select((h, i) => (new Uri(UrlOf(i)).Host, h)).ToList());
        var clock = new MovableClock(NidApp.GoldenCaseToday.AtMidnight().InUtc().ToInstant());
        var nid = await NidApp.StartAsync(fixture, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IWorkerClient>(_ => new WorkerClient(new HttpClient(routes))));
            services.Replace(ServiceDescriptor.Singleton<IClock>(clock));
            nidServices?.Invoke(services);
        });
        return new PairedWorld(hosts, nid, routes, clock);
    }

    public Task<HttpResponseMessage> RegisterAsync(Guid? asPerson = null, string url = WorkerUrl, string placement = "local")
        => Nid.RawAsync(asPerson is null ? Nid.AdultA : new Core.Contracts.PersonId(asPerson.Value), HttpMethod.Post, "/api/v1/ai/workers",
            new { name = "GPU box", url, placement, proxyDeclared = false });

    public async Task<WorkerView> PairAsync(int index = 0, string placement = "local")
    {
        var registered = await (await RegisterAsync(url: UrlOf(index), placement: placement)).Content.ReadFromJsonAsync<RegisteredWorker>(NidJson);
        var paired = await Nid.RawAsync(Nid.AdultA, HttpMethod.Post, $"/api/v1/ai/workers/{registered!.Id}/pair",
            new { fingerprint = registered.Fingerprint, code = Workers[index].PairingCode() });
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
        foreach (var worker in Workers)
            await worker.DisposeAsync();
    }
}

public sealed class RoutingHandler(IReadOnlyList<(string Host, WorkerHost Worker)> hosts) : DelegatingHandler
{
    private readonly HashSet<string> _down = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HttpMessageInvoker> _invokers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public void Down(string host)
    {
        lock (_lock)
            _down.Add(host);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpMessageInvoker invoker;
        lock (_lock)
        {
            var host = request.RequestUri!.Host;
            if (_down.Contains(host) || hosts.All(h => !string.Equals(h.Host, host, StringComparison.OrdinalIgnoreCase)))
                throw new HttpRequestException($"{host} is unreachable.");
            if (!_invokers.TryGetValue(host, out invoker!))
                _invokers[host] = invoker = new HttpMessageInvoker(hosts.First(h => string.Equals(h.Host, host, StringComparison.OrdinalIgnoreCase)).Worker.Server.CreateHandler());
        }

        return invoker.SendAsync(request, cancellationToken);
    }
}

public sealed class MovableClock(Instant start) : IClock
{
    public Instant Now { get; set; } = start;

    public Instant GetCurrentInstant() => Now;
}

public sealed class WorkerHost(Action<IServiceCollection>? services = null) : WebApplicationFactory<WorkerProgram>
{
    public string DataPath { get; } = Directory.CreateTempSubdirectory("nid-worker-").FullName;

    public TestClock Clock { get; } = new();

    public WorkerStateStore State => Services.GetRequiredService<WorkerStateStore>();

    public string PairingCode() => State.Load().PairingCode!;

    public int Run(params string[] command) => worker::Nid.Ai.Worker.Cli.Run(command, State, TextWriter.Null);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Worker:DataPath", DataPath);
        builder.ConfigureServices(s =>
        {
            s.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock));
            services?.Invoke(s);
        });
    }
}

public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => Now;
}
