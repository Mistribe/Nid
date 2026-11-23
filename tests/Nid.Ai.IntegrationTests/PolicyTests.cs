extern alias worker;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Domain;
using Nid.Ai.Infrastructure.Persistence;
using Nid.Ai.Protocol.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional;
using WorkerJobHandler = worker::Nid.Ai.Worker.IJobHandler;

namespace Nid.Ai.IntegrationTests;

public sealed class PolicyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Nothing_reaches_a_remote_worker_before_the_households_opt_in()
    {
        var notices = new Notices();
        await using var world = await PairedWorld.StartAsync(fixture, nidServices: s => s.Replace(ServiceDescriptor.Scoped<INotifier>(_ => notices)));
        await world.PairAsync(placement: "remote");
        var id = await SubmitAsync(world, "documents");

        await PassAsync(world);
        var waiting = await ViewAsync(world, id);
        Assert.Equal("queued", waiting.Status);
        Assert.Equal(DataPolicy.RemoteNotAllowed, waiting.WaitingReason);

        // An adult who is not an admin decides it for the household (docs/ai/README.md §12).
        var opened = await world.Nid.RawAsync(world.Nid.AdultB, HttpMethod.Put, "/api/v1/ai/settings/remote-workers", new { allowed = true });
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.True((await opened.Content.ReadFromJsonAsync<AiSettingsView>(PairedWorld.NidJson, TestContext.Current.CancellationToken))!.RemoteWorkersAllowed);

        world.Clock.Now += Duration.FromSeconds(31);
        await PassAsync(world);
        Assert.Equal("running", (await ViewAsync(world, id)).Status);

        await using var scope = Scope(world);
        var consent = await scope.ServiceProvider.GetRequiredService<AiDbContext>().Consents.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(world.Nid.AdultB.Value, consent.PersonId);
        Assert.True(consent.Value);
        var notice = Assert.Single(notices.Published);
        Assert.Equal("ai.data_policy_changed", notice.Kind);
        Assert.IsType<Recipients.AllAdults>(notice.Recipients);
        Assert.Contains("ai_remote_workers_changed", await SecurityEventsAsync(world));
    }

    [Fact]
    public async Task Local_only_data_waits_for_a_worker_an_adult_marked_safe()
    {
        var notices = new Notices();
        await using var world = await PairedWorld.StartAsync(fixture, nidServices: s => s.Replace(ServiceDescriptor.Scoped<INotifier>(_ => notices)));
        var worker = await world.PairAsync();
        var id = await SubmitAsync(world, "local_only");

        await PassAsync(world);
        Assert.Equal(DataPolicy.NoSafeWorker, (await ViewAsync(world, id)).WaitingReason);

        var marked = await world.Nid.RawAsync(world.Nid.AdultB, HttpMethod.Put, $"/api/v1/ai/workers/{worker.Id}/safe", new { safe = true });
        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);
        Assert.True((await marked.Content.ReadFromJsonAsync<WorkerView>(PairedWorld.NidJson, TestContext.Current.CancellationToken))!.Safe);

        world.Clock.Now += Duration.FromSeconds(31);
        await PassAsync(world);
        Assert.Equal("running", (await ViewAsync(world, id)).Status);
        Assert.Equal("ai.data_policy_changed", Assert.Single(notices.Published).Kind);
        Assert.Contains("model_worker_changed", await SecurityEventsAsync(world));
    }

    [Fact]
    public async Task A_change_that_changes_nothing_records_and_tells_nothing()
    {
        var notices = new Notices();
        await using var world = await PairedWorld.StartAsync(fixture, nidServices: s => s.Replace(ServiceDescriptor.Scoped<INotifier>(_ => notices)));
        var worker = await world.PairAsync();

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, "/api/v1/ai/settings/remote-workers", new { allowed = true })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, $"/api/v1/ai/workers/{worker.Id}/safe", new { safe = true })).StatusCode);
        }

        await using var scope = Scope(world);
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AiDbContext>().Consents.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, notices.Published.Count);
        var events = await SecurityEventsAsync(world);
        Assert.Single(events, e => e == "ai_remote_workers_changed");
        Assert.Single(events, e => e == "model_worker_changed");
    }

    [Fact]
    public async Task Adults_toggling_at_once_never_get_an_error()
    {
        await using var world = await PairedWorld.StartAsync(fixture);

        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => world.Nid.RawAsync(
            i % 2 == 0 ? world.Nid.AdultA : world.Nid.AdultB, HttpMethod.Put, "/api/v1/ai/settings/remote-workers", new { allowed = i % 4 < 2 })));

        Assert.All(answers, a => Assert.Contains(a.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
    }

    [Fact]
    public async Task Withdrawing_safe_takes_back_local_only_work_already_sent()
    {
        var gate = new Gate();
        await using var world = await PairedWorld.StartAsync(fixture, workerServices: s => s.AddSingleton<WorkerJobHandler>(gate),
            nidServices: s => s.AddSingleton<IModelContract>(new TestContract("test-gate", allowsNoData: false)));
        var worker = await world.PairAsync();
        await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, $"/api/v1/ai/workers/{worker.Id}/safe", new { safe = true });
        var id = await SubmitAsync(world, "local_only", "test-gate", new { n = 1 }, allowsNoData: false, contract: true).Accepted();
        await PassAsync(world);
        Assert.Equal("running", (await ViewAsync(world, id)).Status);

        await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, $"/api/v1/ai/workers/{worker.Id}/safe", new { safe = false });
        world.Clock.Now += Duration.FromSeconds(6);
        await PassAsync(world);

        var view = await ViewAsync(world, id);
        Assert.Equal("queued", view.Status);
        Assert.Equal(DataPolicy.NoSafeWorker, view.WaitingReason);
        Assert.Equal(0, view.Attempts);
        await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_data_is_only_for_a_contract_that_carries_none()
    {
        await using var world = await PairedWorld.StartAsync(fixture);

        var refused = await SubmitAsync(world, "none", "test-gate", new { n = 1 }, allowsNoData: false, contract: true).Refused();

        Assert.Equal(JobCodes.UnknownDataClass, refused);
    }

    [Fact]
    public async Task The_settings_are_read_by_any_adult()
    {
        await using var world = await PairedWorld.StartAsync(fixture);

        var read = await world.Nid.RawAsync(world.Nid.AdultB, HttpMethod.Get, "/api/v1/ai/settings");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.False((await read.Content.ReadFromJsonAsync<AiSettingsView>(PairedWorld.NidJson, TestContext.Current.CancellationToken))!.RemoteWorkersAllowed);
    }

    private static AsyncServiceScope Scope(PairedWorld world)
    {
        var scope = world.Nid.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    private static async Task<Guid> SubmitAsync(PairedWorld world, string dataClass)
    {
        await using var scope = Scope(world);
        return await scope.ServiceProvider.GetRequiredService<IModelJobs>().SubmitAsync(
            new JobRequest("echo", 1, JsonSerializer.SerializeToElement(new EchoJobInput("hello"), ContractJson.Strict), dataClass),
            TestContext.Current.CancellationToken).Accepted();
    }

    /// <summary>A job of a test type, through a contract registered for this call only.</summary>
    private static async Task<Result<Guid>> SubmitAsync(PairedWorld world, string dataClass, string type, object input, bool allowsNoData, bool contract)
    {
        await using var scope = Scope(world);
        var jobs = new ModelJobs(scope.ServiceProvider.GetRequiredService<IJobStore>(),
            new ModelContracts(contract ? [new TestContract(type, allowsNoData)] : []),
            scope.ServiceProvider.GetRequiredService<NodaTime.IClock>());
        return await jobs.SubmitAsync(new JobRequest(type, 1, JsonSerializer.SerializeToElement(input), dataClass), TestContext.Current.CancellationToken);
    }

    private static async Task<ModelJobView> ViewAsync(PairedWorld world, Guid id)
    {
        await using var scope = Scope(world);
        return (await scope.ServiceProvider.GetRequiredService<IModelJobs>().GetAsync(id, TestContext.Current.CancellationToken))!;
    }

    private static async Task PassAsync(PairedWorld world)
    {
        await using var scope = Scope(world);
        await scope.ServiceProvider.GetRequiredService<IJobDispatcher>().RunOnceAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<string>> SecurityEventsAsync(PairedWorld world)
    {
        await using var scope = Scope(world);
        return await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().AuditLog.AsNoTracking()
            .Where(e => e.Category == "security").Select(e => e.Action).ToListAsync(TestContext.Current.CancellationToken);
    }

    private sealed class TestContract(string type, bool allowsNoData) : IModelContract
    {
        public ModelCallKind Kind => ModelCallKind.Job;

        public string Type => type;

        public int Version => 1;

        public bool AllowsNoData => allowsNoData;

        public bool ValidInput(ReadOnlySpan<byte> input) => true;

        public string? ReadOutput(ReadOnlySpan<byte> output) => System.Text.Encoding.UTF8.GetString(output);
    }

    private sealed class Gate : WorkerJobHandler
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Type => "test-gate";

        public int Version => 1;

        public async Task<byte[]> RunAsync(JsonElement input, byte[]? file, CancellationToken ct)
        {
            try
            {
                await _open.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }

            return """{"done": true}"""u8.ToArray();
        }
    }

    private sealed class Notices : INotifier
    {
        private readonly List<Notice> _published = [];

        public IReadOnlyList<Notice> Published
        {
            get
            {
                lock (_published)
                    return [.. _published];
            }
        }

        public Task PublishAsync(Notice notice, CancellationToken ct)
        {
            lock (_published)
                _published.Add(notice);
            return Task.CompletedTask;
        }

        public Task ResolveAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }
}
