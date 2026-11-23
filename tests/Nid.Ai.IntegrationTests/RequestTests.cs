extern alias worker;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Protocol.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using WorkerRequestHandler = worker::Nid.Ai.Worker.IRequestHandler;

namespace Nid.Ai.IntegrationTests;

public sealed class RequestTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_streamed_echo_comes_back_frame_by_frame()
    {
        await using var world = await PairedWorld.StartAsync(fixture);
        await world.PairAsync();

        var frames = await StreamAsync(world, new EchoRequestInput("bonjour", 3));

        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.Equal("bonjour", ContractJson.Read<EchoRequestOutput>(f).Text));
    }

    [Fact]
    public async Task A_worker_that_fails_mid_stream_is_an_error_not_a_short_answer()
    {
        await using var world = await PairedWorld.StartAsync(fixture,
            workerServices: s => s.AddSingleton<WorkerRequestHandler>(new FailsAfterOne()));
        await world.PairAsync();

        var failed = await Assert.ThrowsAsync<ModelRefused>(() => StreamAsync(world, new EchoRequestInput("x", 5)));

        Assert.Single(failed.Before);
        Assert.StartsWith(JobCodes.WorkerFailed, failed.Code);
    }

    [Fact]
    public async Task Tester_reports_why_the_echo_failed_and_queues_no_job()
    {
        await using var world = await PairedWorld.StartAsync(fixture,
            workerServices: s => s.AddSingleton<WorkerRequestHandler>(new FailsAfterOne()));
        var worker = await world.PairAsync();

        var response = await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Post, $"/api/v1/ai/workers/{worker.Id}/test");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(JobCodes.WorkerFailed, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var jobs = await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Get, "/api/v1/ai/jobs");
        Assert.Empty((await jobs.Content.ReadFromJsonAsync<ModelJobPage>(PairedWorld.NidJson, TestContext.Current.CancellationToken))!.Items);
    }

    [Fact]
    public async Task Without_an_eligible_worker_a_request_fails_at_once()
    {
        await using var world = await PairedWorld.StartAsync(fixture);

        var failed = await Assert.ThrowsAsync<ModelRefused>(() => StreamAsync(world, new EchoRequestInput("x", 1)));

        Assert.Equal(JobCodes.NoWorker, failed.Code);
    }

    [Fact]
    public async Task A_request_goes_to_the_next_worker_when_the_first_is_out_of_reach()
    {
        await using var world = await PairedWorld.StartAsync(fixture, workers: 2);
        await world.PairAsync(0);
        await world.PairAsync(1);
        world.Routes.Down(new Uri(PairedWorld.UrlOf(0)).Host);
        world.Routes.Down(new Uri(PairedWorld.UrlOf(1)).Host);
        var failed = await Assert.ThrowsAsync<ModelRefused>(() => StreamAsync(world, new EchoRequestInput("x", 1)));
        Assert.Equal(JobCodes.WorkerFailed, failed.Code);

        await using var other = await PairedWorld.StartAsync(fixture, workers: 2);
        await other.PairAsync(0);
        await other.PairAsync(1);
        other.Routes.Down(new Uri(PairedWorld.UrlOf(0)).Host);
        Assert.Single(await StreamAsync(other, new EchoRequestInput("x", 1)));
    }

    [Fact]
    public async Task An_input_over_its_contract_or_its_size_is_refused_before_any_call()
    {
        await using var world = await PairedWorld.StartAsync(fixture);
        await world.PairAsync();

        var bad = await Assert.ThrowsAsync<ModelRefused>(() => StreamAsync(world, new { text = "x" }));

        Assert.Equal(JobCodes.InvalidInput, bad.Code);
    }

    [Fact]
    public async Task Tester_streams_an_echo_and_queues_a_job_for_that_worker()
    {
        await using var world = await PairedWorld.StartAsync(fixture);
        var worker = await world.PairAsync();

        var response = await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Post, $"/api/v1/ai/workers/{worker.Id}/test");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tested = await response.Content.ReadFromJsonAsync<WorkerTestResult>(PairedWorld.NidJson, TestContext.Current.CancellationToken);
        Assert.Equal(3, tested!.Frames);

        for (var i = 0; i < 20; i++)
        {
            await using (var scope = Scope(world))
                await scope.ServiceProvider.GetRequiredService<IJobDispatcher>().RunOnceAsync(TestContext.Current.CancellationToken);
            await Task.Delay(20, TestContext.Current.CancellationToken);
            world.Clock.Now += Duration.FromSeconds(6);
        }

        var job = await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Get, $"/api/v1/ai/jobs/{tested.JobId}");
        var view = await job.Content.ReadFromJsonAsync<ModelJobView>(PairedWorld.NidJson, TestContext.Current.CancellationToken);
        Assert.Equal("succeeded", view!.Status);
        Assert.Equal(worker.Id, view.WorkerId);

        var refused = await world.Nid.RawAsync(world.Nid.AdultB, HttpMethod.Post, $"/api/v1/ai/workers/{worker.Id}/test");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    private static AsyncServiceScope Scope(PairedWorld world)
    {
        var scope = world.Nid.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    private static async Task<List<byte[]>> StreamAsync(PairedWorld world, object input)
    {
        await using var scope = Scope(world);
        var frames = await scope.ServiceProvider.GetRequiredService<IModelRequests>()
            .StreamAsync("echo", 1, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(input, ContractJson.Strict), "none", null,
                TestContext.Current.CancellationToken).ReadAllAsync();
        return [.. frames.Select(f => f.ToArray())];
    }

    private sealed class FailsAfterOne : WorkerRequestHandler
    {
        public string Type => "echo";

        public int Version => 1;

        public async IAsyncEnumerable<byte[]> RunAsync(byte[] input, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return ContractJson.Write(new EchoRequestOutput("x"));
            await Task.Yield();
            throw new InvalidOperationException("the model fell over");
        }
    }
}
