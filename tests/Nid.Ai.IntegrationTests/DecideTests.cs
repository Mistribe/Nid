extern alias worker;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Ai.Contracts;
using Nid.Ai.Infrastructure.Persistence;
using Nid.Ai.Worker.Tests;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using UnambitiousFx.Functional;
using EngineOptions = worker::Nid.Ai.Worker.EngineOptions;
using EngineTransport = worker::Nid.Ai.Worker.EngineTransport;

namespace Nid.Ai.IntegrationTests;

/// <summary>#450: the gateway puts short texts in one of the options through the <c>decide</c> use's model on a paired worker, within the
/// data policy and the consent of the adults concerned, and records the call; no cloud serves it.</summary>
public sealed class DecideTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Model = "qwen2.5:3b";
    private static readonly IReadOnlyList<string> Texts = ["CB LIDL", "PRLV LOYER"];
    private static readonly IReadOnlyList<string> Options = ["Alimentation › Courses", "Logement › Loyer", "Aucune de ces catégories"];

    [Fact]
    public async Task Texts_are_decided_by_the_use_model_and_recorded_without_their_content()
    {
        var engine = Engine();
        await using var world = await StartAsync(engine);
        var paired = await world.PairAsync();
        await using (var scope = Scope(world))
            Assert.Null(await scope.ServiceProvider.GetRequiredService<IDecisions>().ModelAsync(TestContext.Current.CancellationToken));
        Assert.Equal(JobCodes.UseOff, Refused(await DecideAsync(world, new LlmDecideRequest(Texts, Options, DataClasses.BankLabels))));
        await SetUseAsync(world);

        var decided = Ok(await DecideAsync(world, new LlmDecideRequest(Texts, Options, DataClasses.BankLabels)));

        Assert.Equal((Model, "llm-choice", "1", "local"), (decided.Model, decided.Implementation, decided.Version, decided.Placement));
        Assert.StartsWith("sha256:", decided.Digest);
        // The fake engine writes B with B and A among its likeliest: places 1 and 0.
        Assert.All(decided.Decisions, d =>
        {
            Assert.Equal(1, d.Choice);
            Assert.Equal([(0, -1.9), (1, -0.2)], d.Scores.Select(s => (s.Key, s.Value)).OrderBy(s => s.Key));
        });
        Assert.Equal(2, engine.ChatRequests.Count);
        var call = Assert.Single(await CallsAsync(world), c => c.Status == "succeeded");
        Assert.Equal(("decide", "decide", Model, paired.Id), (call.Task, call.Use, call.Model, call.WorkerId));

        await using var reads = Scope(world);
        var decisions = reads.ServiceProvider.GetRequiredService<IDecisions>();
        Assert.Equal(Model, await decisions.ModelAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new LlmDecideImplementation("llm-choice", "1", false), await decisions.ImplementationAsync(Model, TestContext.Current.CancellationToken));
        Assert.Null(await decisions.ImplementationAsync("llama3.2", TestContext.Current.CancellationToken));
        Assert.StartsWith("sha256:", await reads.ServiceProvider.GetRequiredService<IModelDigests>().DigestAsync("decide", Model, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task It_reaches_a_remote_worker_only_once_each_adult_concerned_allows_it()
    {
        var engine = Engine();
        await using var world = await StartAsync(engine);
        await world.PairAsync(placement: "remote");
        await SetUseAsync(world);
        await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, "/api/v1/ai/settings/remote-workers", new { allowed = true });
        var request = new LlmDecideRequest(Texts, Options, DataClasses.BankLabels, Concerned: [world.Nid.AdultA.Value]);

        Assert.Equal(JobCodes.OffsiteNotAllowed, Refused(await DecideAsync(world, request)));
        Assert.Empty(engine.ChatRequests);

        Assert.Equal(HttpStatusCode.OK, (await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, "/api/v1/ai/assistant/me/offsite",
            new { preference = "allow" })).StatusCode);
        Assert.Equal("remote", Ok(await DecideAsync(world, request)).Placement);
    }

    [Fact]
    public async Task Too_many_options_or_a_cloud_mode_send_nothing_more()
    {
        var engine = Engine();
        await using var world = await StartAsync(engine);
        await world.PairAsync();
        await SetUseAsync(world);

        Assert.Equal(JobCodes.InvalidInput, Refused(await DecideAsync(world,
            new LlmDecideRequest(Texts, [.. Enumerable.Range(0, 53).Select(i => $"Catégorie {i}")], DataClasses.BankLabels))));
        Assert.Equal(JobCodes.InvalidInput, Refused(await DecideAsync(world,
            new LlmDecideRequest([new string('x', 401)], Options, DataClasses.BankLabels))));
        Assert.Empty(engine.ChatRequests);

        // With the cloud allowed, decide still goes to the workers only: none answering, no cloud either.
        Assert.Equal(HttpStatusCode.OK, (await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, "/api/v1/ai/settings/uses/decide",
            new { mode = "self_hosted_then_cloud" })).StatusCode);
        engine.Down = true;
        Assert.Equal(Nid.Ai.Domain.JobRetries.WorkerFailed("engine_unreachable"),
            Refused(await DecideAsync(world, new LlmDecideRequest(Texts, Options, DataClasses.BankLabels))));
    }

    private static FakeEngine Engine()
    {
        var engine = new FakeEngine();
        engine.Models.Add(new JsonObject { ["id"] = Model, ["object"] = "model" });
        return engine;
    }

    private Task<PairedWorld> StartAsync(FakeEngine engine)
        => PairedWorld.StartAsync(fixture, workerServices: s =>
        {
            s.Replace(ServiceDescriptor.Singleton(new EngineTransport(engine)));
            s.Configure<EngineOptions>(o =>
            {
                o.Url = "http://engine:11434";
                o.DecideModels = Model;
            });
        });

    private static async Task SetUseAsync(PairedWorld world)
    {
        Assert.Equal(HttpStatusCode.OK, (await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, "/api/v1/ai/settings/uses/decide",
            new { mode = "self_hosted" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, "/api/v1/ai/settings/uses/decide/model",
            new { model = Model })).StatusCode);
    }

    private static async Task<Result<LlmDecisions>> DecideAsync(PairedWorld world, LlmDecideRequest request)
    {
        await using var scope = Scope(world);
        return await scope.ServiceProvider.GetRequiredService<IDecisions>().DecideAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<List<LlmCallRow>> CallsAsync(PairedWorld world)
    {
        await using var scope = Scope(world);
        return await scope.ServiceProvider.GetRequiredService<AiDbContext>().LlmCalls.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private static AsyncServiceScope Scope(PairedWorld world)
    {
        var scope = world.Nid.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    private static T Ok<T>(Result<T> result) where T : notnull
    {
        Assert.True(result.TryGetValue(out var value), result.TryGetFailure(out var failure) ? failure!.Code : "");
        return value!;
    }

    private static string Refused<T>(Result<T> result) where T : notnull
    {
        Assert.True(result.TryGetFailure(out var failure), "the call went through");
        return failure!.Code;
    }
}
