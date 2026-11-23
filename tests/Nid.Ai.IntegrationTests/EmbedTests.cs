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

/// <summary>#558: the gateway embeds a batch of texts through the <c>embed</c> use's model on a paired worker, within the data
/// policy and the consent of the adults concerned, and records the call.</summary>
public sealed class EmbedTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly IReadOnlyList<string> Texts = ["Avis d'échéance", "Quittance de loyer"];

    [Fact]
    public async Task A_batch_is_embedded_by_the_use_model_and_recorded_without_its_content()
    {
        var engine = Engine();
        await using var world = await StartAsync(engine);
        var paired = await world.PairAsync();
        await SetModelAsync(world);

        var embedded = Ok(await EmbedAsync(world, new LlmEmbedRequest(Texts, DataClasses.Documents)));

        Assert.Equal(("nomic-embed-text", 4), (embedded.Model, embedded.Dimension));
        Assert.Equal([15f, 18f], embedded.Vectors.Select(v => v[0]));
        Assert.Equal(Texts, engine.EmbedRequests.Single()["input"]!.AsArray().Select(t => t!.GetValue<string>()));
        var call = Assert.Single(await CallsAsync(world));
        Assert.Equal(("embed", "embed", "succeeded", "nomic-embed-text", paired.Id), (call.Task, call.Use, call.Status, call.Model, call.WorkerId));
    }

    [Fact]
    public async Task Without_a_model_set_nothing_is_sent()
    {
        var engine = Engine();
        await using var world = await StartAsync(engine);
        await world.PairAsync();

        Assert.Equal(JobCodes.NoModel, Refused(await EmbedAsync(world, new LlmEmbedRequest(Texts, DataClasses.Documents))));
        Assert.Empty(engine.EmbedRequests);
    }

    [Fact]
    public async Task It_reaches_a_remote_worker_only_once_each_adult_concerned_allows_it()
    {
        var engine = Engine();
        await using var world = await StartAsync(engine);
        await world.PairAsync(placement: "remote");
        await SetModelAsync(world);
        await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, "/api/v1/ai/settings/remote-workers", new { allowed = true });
        var request = new LlmEmbedRequest(Texts, DataClasses.Documents, Concerned: [world.Nid.AdultA.Value, world.Nid.AdultB.Value]);

        Assert.Equal(JobCodes.OffsiteNotAllowed, Refused(await EmbedAsync(world, request)));
        Assert.Empty(engine.EmbedRequests);

        await AllowAsync(world, world.Nid.AdultA);
        await AllowAsync(world, world.Nid.AdultB);
        Assert.Equal(2, Ok(await EmbedAsync(world, request)).Vectors.Count);
    }

    [Fact]
    public async Task Local_only_data_goes_to_a_worker_marked_safe_only()
    {
        var engine = Engine();
        await using var world = await StartAsync(engine);
        await world.PairAsync();
        await SetModelAsync(world);

        Assert.Equal(JobCodes.NoSafeWorker, Refused(await EmbedAsync(world, new LlmEmbedRequest(Texts, DataClasses.LocalOnly))));
        Assert.Empty(engine.EmbedRequests);
    }

    [Fact]
    public async Task The_cloud_mode_is_refused_for_embed_until_its_providers_exist()
    {
        await using var world = await StartAsync(Engine());

        var response = await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, "/api/v1/ai/settings/uses/embed", new { mode = "self_hosted_then_cloud" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(JobCodes.CloudNotAvailable, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    private static FakeEngine Engine()
    {
        var engine = new FakeEngine();
        engine.Models.Add(new JsonObject { ["id"] = "nomic-embed-text", ["object"] = "model" });
        return engine;
    }

    private Task<PairedWorld> StartAsync(FakeEngine engine)
        => PairedWorld.StartAsync(fixture, workerServices: s =>
        {
            s.Replace(ServiceDescriptor.Singleton(new EngineTransport(engine)));
            s.Configure<EngineOptions>(o =>
            {
                o.Url = "http://engine:11434";
                o.EmbedModels = "nomic-embed-text=4";
            });
        });

    private static async Task SetModelAsync(PairedWorld world)
        => Assert.Equal(HttpStatusCode.OK, (await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, "/api/v1/ai/settings/uses/embed/model",
            new { model = "nomic-embed-text" })).StatusCode);

    private static async Task AllowAsync(PairedWorld world, Core.Contracts.PersonId adult)
        => Assert.Equal(HttpStatusCode.OK, (await world.Nid.RawAsync(adult, HttpMethod.Put, "/api/v1/ai/assistant/me/offsite", new { preference = "allow" })).StatusCode);

    private static async Task<Result<LlmEmbeddings>> EmbedAsync(PairedWorld world, LlmEmbedRequest request)
    {
        await using var scope = Scope(world);
        return await scope.ServiceProvider.GetRequiredService<IEmbeddings>().EmbedAsync(request, TestContext.Current.CancellationToken);
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
