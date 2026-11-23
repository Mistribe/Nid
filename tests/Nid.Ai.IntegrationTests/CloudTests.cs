using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Infrastructure.Cloud;
using Nid.Ai.Infrastructure.Persistence;
using Nid.Ai.Protocol.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Ai.IntegrationTests;

/// <summary>#487: a cloud provider is reached only once an admin enabled its profile, under its terms, with its key, and only
/// at its host; the key never comes back out.</summary>
public sealed class CloudTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Key = "sk-ant-test-0123456789abcdef-WXYZ";
    private static readonly IReadOnlyList<ChatMessage> Hello = [new(ChatRole.System, "Tu es l'assistant de Nid."), new(ChatRole.User, "Bonjour")];

    [Fact]
    public async Task Nothing_leaves_before_a_profile_is_enabled()
    {
        var anthropic = new FakeAnthropic();
        await using var app = await StartAsync(anthropic);
        await SetCloudModeAsync(app);

        var failed = await Assert.ThrowsAsync<ModelRefused>(() => ChatAsync(app, "figures"));

        Assert.Equal(JobCodes.NoCloudProfile, failed.Code);
        Assert.Empty(anthropic.Requests);
    }

    [Fact]
    public async Task An_enabled_profile_serves_the_chat_at_its_host_and_the_call_names_it()
    {
        var anthropic = new FakeAnthropic();
        anthropic.Turn(["Je regarde ", "vos enveloppes."], ("toolu_1", "list_envelopes", ["{\"ow", "ner\":\"me\"}"]), stop: "tool_use", tokensIn: 30, tokensOut: 9);
        await using var app = await StartAsync(anthropic);
        await EnableAsync(app);
        await SetCloudModeAsync(app);
        var tool = new ChatTool("list_envelopes", "Les enveloppes.", System.Text.Json.JsonDocument.Parse("""{"type":"object"}""").RootElement);

        var outputs = await ChatAsync(app, "figures", [tool]);

        Assert.Equal("Je regarde vos enveloppes.", string.Concat(outputs.Select(o => o.Text)));
        Assert.Equal(new ChatToolCall("toolu_1", "list_envelopes", "{\"owner\":\"me\"}"), Assert.Single(outputs, o => o.ToolCall is not null).ToolCall);
        Assert.Equal(new ChatUsage(30, 9, ChatStop.ToolCalls, "claude-opus-5-5"), outputs[^1].Usage);

        var (uri, headers, body) = Assert.Single(anthropic.Requests);
        Assert.Equal("https://api.anthropic.com/v1/messages", uri.ToString());
        Assert.Equal(Key, headers["x-api-key"]);
        Assert.Equal("2026-10-21", headers["anthropic-version"]);
        Assert.Equal("claude-opus-5-5", body["model"]!.GetValue<string>());
        Assert.Equal("Tu es l'assistant de Nid.", body["system"]!.GetValue<string>());
        Assert.False(body.ContainsKey("temperature"));
        Assert.Equal("list_envelopes", body["tools"]![0]!["name"]!.GetValue<string>());

        await using var scope = Scope(app);
        var call = await scope.ServiceProvider.GetRequiredService<AiDbContext>().LlmCalls.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(("anthropic-claude", "anthropic", "paid", "claude-opus-5-5", "succeeded", 30, 9),
            (call.ProfileId, call.Provider, call.Tier, call.Model, call.Status, call.TokensIn, call.TokensOut));
    }

    [Fact]
    public async Task A_cloud_turn_names_its_provider_and_a_turn_pinned_to_a_worker_never_reaches_it()
    {
        var anthropic = new FakeAnthropic();
        anthropic.Turn(["Bonjour."], stop: "end_turn", tokensIn: 5, tokensOut: 2);
        await using var app = await StartAsync(anthropic);
        await EnableAsync(app);
        await SetCloudModeAsync(app);
        LlmDestination? served = null;

        await ChatAsync(app, new LlmChatRequest(ModelUses.Chat, Hello, [], 256, "figures", Served: d => served = d));
        var pinned = await Assert.ThrowsAsync<ModelRefused>(() => ChatAsync(app,
            new LlmChatRequest(ModelUses.Chat, Hello, [], 256, "figures", Pin: "local")));

        Assert.Equal(new LlmDestination(LlmPlacement.Cloud, "anthropic", ProfileId: "anthropic-claude", ContextTokens: 200_000), served);
        Assert.True(served!.Offsite);
        // #532: a local conversation never falls back to the cloud, even with no worker left.
        Assert.Equal(JobCodes.DestinationClassUnavailable, pinned.Code);
        Assert.Equal("cloud:anthropic-claude", served.Class);
        Assert.Single(anthropic.Requests);
    }

    [Fact]
    public async Task Terms_reviewed_since_a_profile_was_enabled_must_be_accepted_again()
    {
        var anthropic = new FakeAnthropic();
        await using var app = await StartAsync(anthropic);
        await EnableAsync(app);
        await SetCloudModeAsync(app);
        await using (var scope = Scope(app))
            await scope.ServiceProvider.GetRequiredService<ICloudSettings>().EnableAsync("anthropic-claude", new NodaTime.LocalDate(2026, 1, 1),
                Guid.CreateVersion7(), NodaTime.SystemClock.Instance.GetCurrentInstant(), TestContext.Current.CancellationToken);

        var failed = await Assert.ThrowsAsync<ModelRefused>(() => ChatAsync(app, "figures"));

        Assert.Equal(JobCodes.NoCloudProfile, failed.Code);
        Assert.Empty(anthropic.Requests);
        var raw = await (await app.RawAsync(app.AdultB, HttpMethod.Get, "/api/v1/ai/cloud")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"enabled\":false", raw);
    }

    [Fact]
    public async Task Local_only_data_never_goes_to_the_cloud()
    {
        var anthropic = new FakeAnthropic();
        await using var app = await StartAsync(anthropic);
        await EnableAsync(app);
        await SetCloudModeAsync(app);

        var failed = await Assert.ThrowsAsync<ModelRefused>(() => ChatAsync(app, "local_only"));

        Assert.Equal(JobCodes.CloudNotAllowed, failed.Code);
        Assert.Empty(anthropic.Requests);
    }

    [Fact]
    public async Task A_failing_profile_hands_over_then_rests_for_a_while()
    {
        var anthropic = new FakeAnthropic();
        await using var app = await StartAsync(anthropic, profiles: [Profile("anthropic-claude", "api.anthropic.com"), Profile("anthropic-second", "second.anthropic.test")]);
        await EnableAsync(app, "anthropic-claude");
        await EnableAsync(app, "anthropic-second");
        await SetCloudModeAsync(app);

        for (var i = 0; i < CloudBreaker.Failures; i++)
        {
            anthropic.Status(HttpStatusCode.TooManyRequests); // the first profile
            anthropic.Turn(["ok"]); // the second takes it
            Assert.Equal("ok", string.Concat((await ChatAsync(app, "figures")).Select(o => o.Text)));
        }

        anthropic.Turn(["direct"]);
        Assert.Equal("direct", string.Concat((await ChatAsync(app, "figures")).Select(o => o.Text)));
        Assert.Equal("second.anthropic.test", anthropic.Requests[^1].Uri.Host); // the first rests
    }

    [Fact]
    public async Task A_refused_request_is_not_retried_elsewhere()
    {
        var anthropic = new FakeAnthropic();
        anthropic.Status(HttpStatusCode.BadRequest);
        await using var app = await StartAsync(anthropic, profiles: [Profile("anthropic-claude", "api.anthropic.com"), Profile("anthropic-second", "second.anthropic.test")]);
        await EnableAsync(app, "anthropic-claude");
        await EnableAsync(app, "anthropic-second");
        await SetCloudModeAsync(app);

        var failed = await Assert.ThrowsAsync<ModelRefused>(() => ChatAsync(app, "figures"));

        Assert.Equal(JobCodes.CloudRefused, failed.Code);
        Assert.Single(anthropic.Requests);
    }

    [Fact]
    public async Task A_cut_after_the_first_words_keeps_its_code_and_tries_no_other_profile()
    {
        var anthropic = new FakeAnthropic();
        anthropic.Cut(["Je commence"], "overloaded_error"); // before any output, this one would hand over
        anthropic.Turn(["jamais"]);
        await using var app = await StartAsync(anthropic, profiles: [Profile("anthropic-claude", "api.anthropic.com"), Profile("anthropic-second", "second.anthropic.test")]);
        await EnableAsync(app, "anthropic-claude");
        await EnableAsync(app, "anthropic-second");
        await SetCloudModeAsync(app);

        var failed = await Assert.ThrowsAsync<ModelRefused>(() => ChatAsync(app, "figures"));

        Assert.Equal(JobCodes.CloudFailed, failed.Code);
        Assert.Equal("Je commence", Assert.IsType<ChatRequestOutput>(Assert.Single(failed.Before)).Text);
        Assert.Single(anthropic.Requests);
    }

    [Fact]
    public async Task The_cloud_link_answers_only_its_refusal_for_data_or_a_pin_it_may_not_take()
    {
        await using var app = await StartAsync(new FakeAnthropic());
        await EnableAsync(app);
        await using var scope = Scope(app);
        var cloud = scope.ServiceProvider.GetServices<ILlmProvider>().OfType<CloudProvider>().Single();
        var turn = new ChatRequestInput("claude-opus-5-5", Hello, [], 256);

        var localOnly = await Assert.ThrowsAsync<ModelRefused>(() =>
            cloud.ChatAsync(turn, DataClasses.LocalOnly, new LlmCallTrace(), TestContext.Current.CancellationToken).ReadAllAsync());
        var pinned = await Assert.ThrowsAsync<ModelRefused>(() =>
            cloud.ChatAsync(turn, DataClasses.Figures, new LlmCallTrace { Pin = "local" }, TestContext.Current.CancellationToken).ReadAllAsync());

        Assert.Equal((JobCodes.CloudNotAllowed, JobCodes.NoCloudProfile), (localOnly.Code, pinned.Code));
        Assert.Empty(localOnly.Before.Concat(pinned.Before));
    }

    [Fact]
    public async Task The_key_is_kept_encrypted_and_never_comes_back()
    {
        await using var app = await StartAsync(new FakeAnthropic());
        await EnableAsync(app);

        var raw = await (await app.RawAsync(app.AdultB, HttpMethod.Get, "/api/v1/ai/cloud")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Key, raw);
        Assert.Contains("\"keyHint\":\"WXYZ\"", raw);

        await using var scope = Scope(app);
        var stored = await scope.ServiceProvider.GetRequiredService<AiDbContext>().Credentials.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Key, System.Text.Encoding.UTF8.GetString(stored.SecretEncrypted));
        var audit = await scope.ServiceProvider.GetRequiredService<Nid.Platform.Infrastructure.Persistence.PlatformDbContext>().AuditLog.AsNoTracking()
            .Select(e => e.Data).ToListAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(audit, data => data.Contains(Key, StringComparison.Ordinal) || data.Contains("sk-ant", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enabling_takes_an_admin_the_region_the_current_terms_and_a_key_and_tells_every_adult()
    {
        await using var app = await StartAsync(new FakeAnthropic());
        var enable = new { termsReviewedOn = "2026-11-22" };

        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Post, "/api/v1/ai/cloud/profiles/anthropic-claude/enable", enable)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/ai/cloud/profiles/anthropic-claude/enable", enable)).StatusCode); // no region
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/cloud/region", new { region = "eea_ch_uk" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/ai/cloud/profiles/anthropic-claude/enable", enable)).StatusCode); // no key
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/cloud/keys/anthropic", new { key = Key })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/ai/cloud/profiles/anthropic-claude/enable", new { termsReviewedOn = "2026-01-01" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/ai/cloud/profiles/anthropic-claude/enable", enable)).StatusCode);

        await using var scope = Scope(app);
        var notices = await scope.ServiceProvider.GetRequiredService<Nid.Platform.Infrastructure.Persistence.PlatformDbContext>().Notifications
            .AsNoTracking().CountAsync(n => n.Kind == "ai.data_policy_changed", TestContext.Current.CancellationToken);
        Assert.True(notices >= 2);
    }

    [Fact]
    public async Task Revoking_the_key_disables_its_profiles()
    {
        await using var app = await StartAsync(new FakeAnthropic());
        await EnableAsync(app);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Delete, "/api/v1/ai/cloud/keys/anthropic");

        var profile = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["profiles"]![0]!;
        Assert.False(profile["enabled"]!.GetValue<bool>());
        Assert.Null(profile["keyHint"]);
    }

    [Fact]
    public async Task A_host_outside_the_profiles_is_refused_before_it_leaves()
    {
        var anthropic = new FakeAnthropic();
        using var http = new HttpClient(CloudHttp.Handler(["api.anthropic.com"], anthropic));

        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("https://api.anthropic.com.evil.test/v1/messages", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://api.anthropic.com/v1/messages", TestContext.Current.CancellationToken));
        Assert.Empty(anthropic.Requests);
    }

    [Fact]
    public void The_shipped_profiles_load_and_one_that_trains_on_the_data_is_refused()
    {
        var shipped = new LlmProfileLoader(NullLogger<LlmProfileLoader>.Instance);
        Assert.Contains(shipped.All, p => p.Id == "anthropic-claude" && p.Host == "api.anthropic.com");

        const string trains = """
            {"id":"x","provider":"anthropic","tier":"paid","host":"api.anthropic.com","model":"m","capabilities":["chat"],"contextTokens":200000,
             "dataTerms":{"trainsOnData":{"eea_ch_uk":false,"other":true},"humanReview":false,"retentionDays":null,"retentionSwitches":[],
             "termsUrl":"https://example.test/terms","reviewedOn":"2026-11-22"},"pricing":{"inputPerMillion":1,"outputPerMillion":1,"currency":"USD"}}
            """;
        Assert.Null(LlmProfileLoader.Read(trains));
        Assert.Null(LlmProfileLoader.Read(trains.Replace("\"other\":true", "\"other\":false").Replace("\"tier\"", "\"tierr\"")));
        Assert.NotNull(LlmProfileLoader.Read(trains.Replace("\"other\":true", "\"other\":false")));
        // #527: a profile says how much its model reads, or the assistant could not bound what it sends.
        Assert.Null(LlmProfileLoader.Read(trains.Replace("\"other\":true", "\"other\":false").Replace("\"contextTokens\":200000,", "")));
        Assert.All(shipped.All, p => Assert.True(p.ContextTokens >= 1024, p.Id));
    }

    private static LlmProfile Profile(string id, string host)
        => LlmProfileLoader.Read($$$"""
            {"id":"{{{id}}}","provider":"anthropic","tier":"paid","host":"{{{host}}}","model":"claude-opus-5-5","capabilities":["chat"],"contextTokens":200000,
             "dataTerms":{"trainsOnData":{"eea_ch_uk":false,"other":false},"humanReview":true,"retentionDays":30,"retentionSwitches":[],
             "termsUrl":"https://www.anthropic.com/legal/commercial-terms","reviewedOn":"2026-11-22"},
             "pricing":{"inputPerMillion":4,"outputPerMillion":20,"currency":"EUR"}}
            """)!;

    private Task<NidApp> StartAsync(FakeAnthropic anthropic, IReadOnlyList<LlmProfile>? profiles = null)
    {
        var all = profiles ?? [Profile("anthropic-claude", "api.anthropic.com")];
        return NidApp.StartAsync(fixture, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ILlmProfiles>(new TestProfiles(all)));
            services.Replace(ServiceDescriptor.Singleton(new CloudHttp(new HttpClient(CloudHttp.Handler(all.Select(p => p.Host), anthropic)))));
        });
    }

    private sealed class TestProfiles(IReadOnlyList<LlmProfile> all) : ILlmProfiles
    {
        public IReadOnlyList<LlmProfile> All => all;
    }

    private static async Task EnableAsync(NidApp app, string profile = "anthropic-claude")
    {
        // A paid profile needs a ceiling (#488).
        await app.RawAsync(app.AdultB, HttpMethod.Put, "/api/v1/ai/budget", new { monthlyBudgetEur = "10" });
        await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/cloud/region", new { region = "eea_ch_uk" });
        await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/cloud/keys/anthropic", new { key = Key });
        Assert.Equal(HttpStatusCode.OK,
            (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/ai/cloud/profiles/{profile}/enable", new { termsReviewedOn = "2026-11-22" })).StatusCode);
    }

    private static async Task SetCloudModeAsync(NidApp app)
    {
        Assert.Equal(HttpStatusCode.OK,
            (await app.RawAsync(app.AdultB, HttpMethod.Put, "/api/v1/ai/settings/uses/chat", new { mode = "self_hosted_then_cloud" })).StatusCode);
        await using var scope = Scope(app);
        // No worker in these tests: the chat's model is set directly (the cloud uses its profile's own).
        await scope.ServiceProvider.GetRequiredService<IAiSettings>().SetUseModelAsync(ModelUses.Chat, "llama3.2", TestContext.Current.CancellationToken);
    }

    private static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    private static Task<List<ChatRequestOutput>> ChatAsync(NidApp app, string dataClass, IReadOnlyList<ChatTool>? tools = null)
        => ChatAsync(app, new LlmChatRequest(ModelUses.Chat, Hello, tools ?? [], 256, dataClass));

    private static async Task<List<ChatRequestOutput>> ChatAsync(NidApp app, LlmChatRequest request)
    {
        await using var scope = Scope(app);
        return await scope.ServiceProvider.GetRequiredService<ILlmGateway>().ChatAsync(request, TestContext.Current.CancellationToken).ReadAllAsync();
    }
}
