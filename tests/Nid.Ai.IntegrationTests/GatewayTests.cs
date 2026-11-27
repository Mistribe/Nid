extern alias worker;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Infrastructure.Persistence;
using Nid.Ai.Protocol.Contracts;
using Nid.Ai.Worker.Tests;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using EngineOptions = worker::Nid.Ai.Worker.EngineOptions;
using EngineTransport = worker::Nid.Ai.Worker.EngineTransport;

namespace Nid.Ai.IntegrationTests;

/// <summary>#486: every model call goes through one gateway that checks the use, routes to a worker serving its model and
/// records the call, never its content.</summary>
public sealed class GatewayTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly IReadOnlyList<ChatMessage> Hello = [new(ChatRole.User, "Bonjour, combien sur le Livret A ?")];

    [Fact]
    public async Task A_chat_turn_runs_on_the_model_an_admin_set_and_is_recorded_without_its_content()
    {
        var engine = Engine();
        await using var world = await StartAsync(engine);
        var paired = await world.PairAsync();
        await SetModelAsync(world, "llama3.2");

        var outputs = await ChatAsync(world, new LlmChatRequest(ModelUses.Chat, Hello, [], 128, "documents"), world.Nid.AdultB);

        Assert.Equal("Bonjour Bob.", string.Concat(outputs.Select(o => o.Text)));
        var call = Assert.Single(await CallsAsync(world));
        Assert.Equal(("chat", "chat", "succeeded", 12, 3, "llama3.2"), (call.Task, call.Use, call.Status, call.TokensIn, call.TokensOut, call.Model));
        Assert.Equal(paired.Id, call.WorkerId);
        Assert.NotNull(call.PersonId);
        Assert.Equal("llama3.2", engine.ChatRequests.Single()["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_a_model_set_the_call_says_so_and_is_recorded()
    {
        await using var world = await StartAsync(Engine());
        await world.PairAsync();

        var failed = await Assert.ThrowsAsync<ModelRefused>(() => ChatAsync(world, new LlmChatRequest(ModelUses.Chat, Hello, [], 128, "documents")));

        Assert.Equal(JobCodes.NoModel, failed.Code);
        var call = Assert.Single(await CallsAsync(world));
        Assert.Equal(("no_destination", "ai.no_model"), (call.Status, call.Code));
    }

    [Fact]
    public async Task Straight_to_the_cloud_a_chat_turn_never_reaches_a_worker()
    {
        // #819: a paired worker that declares the model, set as the use's: with the mode cloud and no profile, the turn goes nowhere.
        var engine = Engine();
        await using var world = await StartAsync(engine);
        await world.PairAsync();
        await SetModelAsync(world, "llama3.2");
        Assert.Equal(HttpStatusCode.OK,
            (await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, "/api/v1/ai/settings/uses/chat", new { mode = "cloud" })).StatusCode);

        var failed = await Assert.ThrowsAsync<ModelRefused>(() => ChatAsync(world, new LlmChatRequest(ModelUses.Chat, Hello, [], 128, "figures")));

        Assert.Equal(JobCodes.NoCloudProfile, failed.Code);
        Assert.Empty(engine.ChatRequests);
        Assert.Equal(JobCodes.NoCloudProfile, await ChatReasonAsync(world));
    }

    [Fact]
    public async Task Only_the_chat_may_go_straight_to_the_cloud()
    {
        await using var world = await StartAsync(Engine());

        foreach (var use in new[] { "embed", "extract", "decide" })
            Assert.Equal(HttpStatusCode.UnprocessableEntity,
                (await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, $"/api/v1/ai/settings/uses/{use}", new { mode = "cloud" })).StatusCode);
    }

    [Fact]
    public async Task A_use_turned_off_sends_nothing()
    {
        var engine = Engine();
        await using var world = await StartAsync(engine);
        await world.PairAsync();
        await SetModelAsync(world, "llama3.2");
        Assert.Equal(HttpStatusCode.OK, (await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, "/api/v1/ai/settings/uses/chat", new { mode = "off" })).StatusCode);

        var failed = await Assert.ThrowsAsync<ModelRefused>(() => ChatAsync(world, new LlmChatRequest(ModelUses.Chat, Hello, [], 128, "documents")));

        Assert.Equal(JobCodes.UseOff, failed.Code);
        Assert.Empty(engine.ChatRequests);
    }

    [Fact]
    public async Task With_no_worker_paired_a_call_fails_with_its_reason()
    {
        await using var world = await StartAsync(Engine());
        await using (var scope = Scope(world))
            await scope.ServiceProvider.GetRequiredService<IAiSettings>().SetUseModelAsync(ModelUses.Chat, "llama3.2", TestContext.Current.CancellationToken);

        var failed = await Assert.ThrowsAsync<ModelRefused>(() => ChatAsync(world, new LlmChatRequest(ModelUses.Chat, Hello, [], 128, "documents")));

        Assert.Equal(JobCodes.NoWorker, failed.Code);
        Assert.Equal(HttpStatusCode.OK, (await world.Nid.RawAsync(world.Nid.AdultB, HttpMethod.Get, "/api/v1/ai/settings")).StatusCode);
    }

    [Fact]
    public async Task A_model_no_worker_serves_any_more_is_unavailable()
    {
        var engine = Engine();
        await using var world = await StartAsync(engine);
        await world.PairAsync();
        await SetModelAsync(world, "llama3.2");
        await using (var scope = Scope(world))
            await scope.ServiceProvider.GetRequiredService<IAiSettings>().SetUseModelAsync(ModelUses.Chat, "mistral-small", TestContext.Current.CancellationToken);

        var failed = await Assert.ThrowsAsync<ModelRefused>(() => ChatAsync(world, new LlmChatRequest(ModelUses.Chat, Hello, [], 128, "documents")));

        Assert.Equal(JobCodes.ModelUnavailable, failed.Code);
        Assert.Empty(engine.ChatRequests);
    }

    [Fact]
    public async Task A_remote_worker_needs_the_households_opt_in()
    {
        var engine = Engine();
        await using var world = await StartAsync(engine);
        await world.PairAsync(placement: "remote");
        await SetModelAsync(world, "llama3.2");

        var failed = await Assert.ThrowsAsync<ModelRefused>(() => ChatAsync(world, new LlmChatRequest(ModelUses.Chat, Hello, [], 128, "documents")));

        Assert.Equal(JobCodes.RemoteNotAllowed, failed.Code);
        Assert.Empty(engine.ChatRequests);
    }

    [Fact]
    public async Task A_served_turn_names_its_destination()
    {
        await using var world = await StartAsync(Engine());
        var paired = await world.PairAsync();
        await SetModelAsync(world, "llama3.2");
        LlmDestination? served = null;

        await ChatAsync(world, new LlmChatRequest(ModelUses.Chat, Hello, [], 128, "figures", Served: d => served = d));

        // #527: the context the worker declares for the model that answered.
        Assert.Equal(new LlmDestination(LlmPlacement.Local, paired.Name, WorkerId: paired.Id, ContextTokens: 8192), served);
        Assert.False(served!.Offsite);
    }

    [Fact]
    public async Task A_turn_pinned_to_another_class_reaches_no_worker_and_says_why()
    {
        var engine = Engine();
        await using var world = await StartAsync(engine);
        await world.PairAsync();
        await SetModelAsync(world, "llama3.2");

        var cloud = await Assert.ThrowsAsync<ModelRefused>(() => ChatAsync(world, new LlmChatRequest(ModelUses.Chat, Hello, [], 128, "figures", Pin: "cloud:anthropic-claude")));
        var safe = await Assert.ThrowsAsync<ModelRefused>(() => ChatAsync(world, new LlmChatRequest(ModelUses.Chat, Hello, [], 128, "figures", Pin: "local+safe")));

        Assert.Equal((JobCodes.DestinationClassUnavailable, JobCodes.DestinationClassUnavailable), (cloud.Code, safe.Code));
        Assert.Empty(engine.ChatRequests);
    }

    [Fact]
    public async Task A_turn_pinned_to_a_class_goes_to_any_worker_of_it()
    {
        await using var world = await PairedWorld.StartAsync(fixture, workers: 2, workerServices: s =>
        {
            s.Replace(ServiceDescriptor.Singleton(new EngineTransport(Engine())));
            s.Configure<EngineOptions>(o => o.Url = "http://engine:11434");
        });
        await world.PairAsync();
        var second = await world.PairAsync(index: 1);
        await SetModelAsync(world, "llama3.2");
        world.Routes.Down(new Uri(PairedWorld.UrlOf(0)).Host);
        LlmDestination? served = null;

        await ChatAsync(world, new LlmChatRequest(ModelUses.Chat, Hello, [], 128, "figures", Pin: "local", Served: d => served = d));

        Assert.Equal((second.Id, "local"), (served!.WorkerId!.Value, served.Class));
    }

    [Fact]
    public async Task Tools_are_for_the_chat_only()
    {
        await using var world = await StartAsync(Engine());
        await world.PairAsync();
        var tool = new ChatTool("list_envelopes", "", System.Text.Json.JsonDocument.Parse("{}").RootElement);

        var failed = await Assert.ThrowsAsync<ModelRefused>(() => ChatAsync(world, new LlmChatRequest(ModelUses.Extract, Hello, [tool], 128, "documents")));

        Assert.Equal(JobCodes.TaskNotAllowed, failed.Code);
    }

    [Fact]
    public async Task An_admin_picks_a_declared_model_and_sets_a_use()
    {
        await using var world = await StartAsync(Engine());
        await world.PairAsync();

        var models = await (await world.Nid.RawAsync(world.Nid.AdultB, HttpMethod.Get, "/api/v1/ai/models")).Content
            .ReadFromJsonAsync<DeclaredModels>(PairedWorld.NidJson, TestContext.Current.CancellationToken);
        Assert.Equal(new DeclaredModelView("llama3.2", "chat", 1), Assert.Single(models!.Models));

        Assert.Equal(HttpStatusCode.Forbidden,
            (await world.Nid.RawAsync(world.Nid.AdultB, HttpMethod.Put, "/api/v1/ai/settings/uses/chat/model", new { model = "llama3.2" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, "/api/v1/ai/settings/uses/chat/model", new { model = "gpt-oss" })).StatusCode);
        // #585: a use's mode is the administration's too.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await world.Nid.RawAsync(world.Nid.AdultB, HttpMethod.Put, "/api/v1/ai/settings/uses/chat", new { mode = "off" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, "/api/v1/ai/settings/uses/chat", new { mode = "anywhere" })).StatusCode);

        Assert.Equal(JobCodes.NoModel, await ChatReasonAsync(world));
        await SetModelAsync(world, "llama3.2");
        Assert.Null(await ChatReasonAsync(world));

        var settings = await (await world.Nid.RawAsync(world.Nid.AdultB, HttpMethod.Get, "/api/v1/ai/settings")).Content
            .ReadFromJsonAsync<AiSettingsView>(PairedWorld.NidJson, TestContext.Current.CancellationToken);
        Assert.Contains(new UseSettingView("chat", "self_hosted", "llama3.2"), settings!.Uses);
    }

    [Fact]
    public async Task Opening_a_use_to_the_cloud_tells_every_adult()
    {
        await using var world = await StartAsync(Engine());

        var response = await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, "/api/v1/ai/settings/uses/chat", new { mode = "self_hosted_then_cloud" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = Scope(world);
        var notices = await scope.ServiceProvider.GetRequiredService<Nid.Platform.Infrastructure.Persistence.PlatformDbContext>().Notifications
            .AsNoTracking().Where(n => n.Kind == "ai.data_policy_changed").CountAsync(TestContext.Current.CancellationToken);
        Assert.True(notices >= 2);
    }

    private static FakeEngine Engine()
    {
        var engine = new FakeEngine();
        engine.Chunks.AddRange([FakeEngine.TextChunk("Bonjour "), FakeEngine.TextChunk("Bob."), FakeEngine.FinishChunk("stop"), FakeEngine.UsageChunk(12, 3)]);
        return engine;
    }

    private Task<PairedWorld> StartAsync(FakeEngine engine)
        => PairedWorld.StartAsync(fixture, workerServices: s =>
        {
            s.Replace(ServiceDescriptor.Singleton(new EngineTransport(engine)));
            s.Configure<EngineOptions>(o => o.Url = "http://engine:11434");
        });

    private static async Task SetModelAsync(PairedWorld world, string model)
        => Assert.Equal(HttpStatusCode.OK,
            (await world.Nid.RawAsync(world.Nid.AdultA, HttpMethod.Put, "/api/v1/ai/settings/uses/chat/model", new { model })).StatusCode);

    private static async Task<string?> ChatReasonAsync(PairedWorld world)
        => (await (await world.Nid.RawAsync(world.Nid.AdultB, HttpMethod.Get, "/api/v1/ai/capabilities")).Content
                .ReadFromJsonAsync<ModelCapabilities>(PairedWorld.NidJson, TestContext.Current.CancellationToken))!
            .Capabilities.Single(c => c.Capability == "chat").Reason;

    private static AsyncServiceScope Scope(PairedWorld world)
    {
        var scope = world.Nid.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    private static async Task<List<ChatRequestOutput>> ChatAsync(PairedWorld world, LlmChatRequest request, Core.Contracts.PersonId? person = null)
    {
        await using var scope = world.Nid.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = person is { } p ? Actor.Browser(p) : Actor.System;
        return await scope.ServiceProvider.GetRequiredService<ILlmGateway>().ChatAsync(request, TestContext.Current.CancellationToken).ReadAllAsync();
    }

    private static async Task<List<LlmCallRow>> CallsAsync(PairedWorld world)
    {
        await using var scope = Scope(world);
        return await scope.ServiceProvider.GetRequiredService<AiDbContext>().LlmCalls.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }
}
