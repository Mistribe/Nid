using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Infrastructure.Cloud;
using Nid.Ai.Infrastructure.Persistence;
using Nid.Ai.Protocol.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Ai.IntegrationTests;

/// <summary>#488: paid providers stop at the household's monthly ceiling whatever asks, concurrent calls never pass it
/// together, both adults hear of 80 % and 100 %, and the workers are never budgeted.</summary>
public sealed class BudgetTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Key = "sk-ant-test-0123456789abcdef-WXYZ";
    private static readonly IReadOnlyList<ChatMessage> Hello = [new(ChatRole.User, "Bonjour")];

    [Fact]
    public async Task Without_a_ceiling_no_paid_call_goes_and_the_call_says_why()
    {
        var anthropic = new FakeAnthropic();
        await using var app = await StartAsync(anthropic);
        await EnableAsync(app);

        var failed = await Assert.ThrowsAsync<ModelCallException>(() => ChatAsync(app));

        Assert.Equal(JobCodes.BudgetBlocked, failed.Code);
        Assert.Empty(anthropic.Requests);
        var call = await SingleCallAsync(app);
        Assert.Equal(("blocked_budget", "ai.budget_blocked"), (call.Status, call.Code));
    }

    [Fact]
    public async Task A_paid_call_holds_its_upper_bound_then_settles_on_the_reported_tokens()
    {
        var anthropic = new FakeAnthropic();
        anthropic.Turn(["Bonjour Alice."], tokensIn: 100, tokensOut: 200);
        await using var app = await StartAsync(anthropic);
        await EnableAsync(app);
        await SetBudgetAsync(app, 10m);

        await ChatAsync(app);

        // 100 tokens in at 4 €/M and 200 out at 20 €/M: 0.0044 €.
        var call = await SingleCallAsync(app);
        Assert.Equal(0.0044m, call.CostEstimateEur);
        Assert.True(call.ReservedEur > call.CostEstimateEur);
        var state = await BudgetAsync(app);
        Assert.Equal((0.0044m, 0m), (state.Spent, state.Reserved));
    }

    [Fact]
    public async Task Concurrent_holds_never_pass_the_ceiling_together()
    {
        await using var app = await StartAsync(new FakeAnthropic());
        await SetBudgetAsync(app, 0.01m);
        var month = BudgetMonths.Of(Now(app));

        var holds = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var scope = Scope(app);
            return await scope.ServiceProvider.GetRequiredService<IBudget>()
                .ReserveAsync(month, 0.004m, Now(app) + Duration.FromMinutes(15), TestContext.Current.CancellationToken);
        }));

        Assert.Equal(2, holds.Count(h => h is not null));
        Assert.Equal(0.008m, (await BudgetAsync(app)).Reserved);
    }

    [Fact]
    public async Task Both_adults_hear_of_80_percent_once_then_of_the_ceiling()
    {
        await using var app = await StartAsync(new FakeAnthropic());
        await SetBudgetAsync(app, 1m);
        var month = BudgetMonths.Of(Now(app));

        foreach (var spend in new[] { 0.5m, 0.35m, 0.05m, 0.1m })
        {
            await using var scope = Scope(app);
            var budget = scope.ServiceProvider.GetRequiredService<IBudget>();
            var hold = await budget.ReserveAsync(month, spend, Now(app) + Duration.FromMinutes(15), TestContext.Current.CancellationToken);
            var settled = await budget.SettleAsync(hold!.Value, spend, TestContext.Current.CancellationToken);
            await scope.ServiceProvider.GetRequiredService<BudgetNotices>().AfterAsync(settled!, TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, await NoticesAsync(app, "ai.budget_alert")); // one per adult, once
        Assert.Equal(2, await NoticesAsync(app, "ai.budget_reached"));
        Assert.Equal("reached", (await ViewAsync(app)).Status);
    }

    [Fact]
    public async Task A_hold_no_answer_settled_is_settled_at_its_amount()
    {
        await using var app = await StartAsync(new FakeAnthropic());
        await SetBudgetAsync(app, 1m);
        var past = Now(app) - Duration.FromHours(1);
        await using (var scope = Scope(app))
            await scope.ServiceProvider.GetRequiredService<IBudget>().ReserveAsync(BudgetMonths.Of(Now(app)), 0.2m, past,
                TestContext.Current.CancellationToken);

        await using (var scope = Scope(app))
            await scope.ServiceProvider.GetRequiredService<IBudgetSweep>().RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal((0.2m, 0m), ((await BudgetAsync(app)).Spent, (await BudgetAsync(app)).Reserved));
    }

    [Fact]
    public async Task Any_adult_sets_the_ceiling_in_cents_and_both_are_told()
    {
        await using var app = await StartAsync(new FakeAnthropic());

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await app.RawAsync(app.AdultB, HttpMethod.Put, "/api/v1/ai/budget", new { monthlyBudgetEur = "10.005" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Put, "/api/v1/ai/budget", new { monthlyBudgetEur = "12.50" })).StatusCode);

        Assert.Equal("12.50", (await ViewAsync(app)).MonthlyBudgetEur);
        Assert.Equal(2, await NoticesAsync(app, "ai.budget_changed"));
    }

    [Fact]
    public async Task A_price_with_no_exchange_rate_blocks_the_paid_call()
    {
        var anthropic = new FakeAnthropic();
        await using var app = await StartAsync(anthropic, currency: "USD");
        await EnableAsync(app);
        await SetBudgetAsync(app, 10m);

        var failed = await Assert.ThrowsAsync<ModelCallException>(() => ChatAsync(app));

        Assert.Equal(JobCodes.BudgetBlocked, failed.Code);
        Assert.Empty(anthropic.Requests);
    }

    private static LlmProfile Profile(string currency)
        => LlmProfileLoader.Read($$$"""
            {"id":"anthropic-claude","provider":"anthropic","tier":"paid","host":"api.anthropic.com","model":"claude-opus-5-5","capabilities":["chat"],"contextTokens":200000,
             "dataTerms":{"trainsOnData":{"eea_ch_uk":false,"other":false},"humanReview":true,"retentionDays":30,"retentionSwitches":[],
             "termsUrl":"https://www.anthropic.com/legal/commercial-terms","reviewedOn":"2026-11-22"},
             "pricing":{"inputPerMillion":4,"outputPerMillion":20,"currency":"{{{currency}}}"}}
            """)!;

    private Task<NidApp> StartAsync(FakeAnthropic anthropic, string currency = "EUR")
    {
        var profile = Profile(currency);
        return NidApp.StartAsync(fixture, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ILlmProfiles>(new TestProfiles([profile])));
            services.Replace(ServiceDescriptor.Singleton(new CloudHttp(new HttpClient(CloudHttp.Handler([profile.Host], anthropic)))));
        });
    }

    private sealed class TestProfiles(IReadOnlyList<LlmProfile> all) : ILlmProfiles
    {
        public IReadOnlyList<LlmProfile> All => all;
    }

    private static async Task EnableAsync(NidApp app)
    {
        await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/cloud/region", new { region = "eea_ch_uk" });
        await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/cloud/keys/anthropic", new { key = Key });
        Assert.Equal(HttpStatusCode.OK,
            (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/ai/cloud/profiles/anthropic-claude/enable", new { termsReviewedOn = "2026-11-22" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await app.RawAsync(app.AdultB, HttpMethod.Put, "/api/v1/ai/settings/uses/chat", new { mode = "self_hosted_then_cloud" })).StatusCode);
        await using var scope = Scope(app);
        await scope.ServiceProvider.GetRequiredService<IAiSettings>().SetUseModelAsync(ModelUses.Chat, "llama3.2", TestContext.Current.CancellationToken);
    }

    private static async Task SetBudgetAsync(NidApp app, decimal amount)
        => Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Put, "/api/v1/ai/budget", new { monthlyBudgetEur = amount.ToString(System.Globalization.CultureInfo.InvariantCulture) })).StatusCode);

    private static Instant Now(NidApp app) => app.Services.GetRequiredService<IClock>().GetCurrentInstant();

    private static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    private static async Task ChatAsync(NidApp app)
    {
        await using var scope = Scope(app);
        await foreach (var _ in scope.ServiceProvider.GetRequiredService<ILlmGateway>()
                           .ChatAsync(new LlmChatRequest(ModelUses.Chat, Hello, [], 256, "figures"), TestContext.Current.CancellationToken))
        {
        }
    }

    private static async Task<LlmCallRow> SingleCallAsync(NidApp app)
    {
        await using var scope = Scope(app);
        return await scope.ServiceProvider.GetRequiredService<AiDbContext>().LlmCalls.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<BudgetState> BudgetAsync(NidApp app)
    {
        await using var scope = Scope(app);
        return await scope.ServiceProvider.GetRequiredService<IBudget>().StateAsync(BudgetMonths.Of(Now(app)),
            TestContext.Current.CancellationToken);
    }

    private static async Task<AiBudgetView> ViewAsync(NidApp app)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(await (await app.RawAsync(app.AdultB, HttpMethod.Get, "/api/v1/ai/budget")).Content
            .ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        return new AiBudgetView(json["monthlyBudgetEur"]!.GetValue<string>(), default, json["spentEur"]!.GetValue<string>(),
            json["reservedEur"]!.GetValue<string>(), json["remainingEur"]!.GetValue<string>(), json["status"]!.GetValue<string>());
    }

    private static async Task<int> NoticesAsync(NidApp app, string kind)
    {
        await using var scope = Scope(app);
        return await scope.ServiceProvider.GetRequiredService<Nid.Platform.Infrastructure.Persistence.PlatformDbContext>().Notifications
            .AsNoTracking().CountAsync(n => n.Kind == kind, TestContext.Current.CancellationToken);
    }
}
