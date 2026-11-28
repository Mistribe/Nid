using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Infrastructure.Cloud;
using Nid.Ai.Infrastructure.Persistence;
using Nid.Ai.Protocol.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using UnambitiousFx.Functional;

namespace Nid.Ai.IntegrationTests;

/// <summary>#812: with <c>self_hosted_then_cloud</c> and a cloud profile's model for <c>decide</c> (Jev), masked bank labels go to that
/// provider only once <c>bank_labels</c> is in <c>allowed_kinds</c> and each adult concerned chose <c>allow</c>; local-only data never;
/// the budget stops it; every call is in <c>llm_call</c> with its profile and cost.</summary>
public sealed class CloudDecideTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Model = "jev-1.14.0";
    private static readonly IReadOnlyList<string> Texts = ["Sens : débit\nMotif : LIDL", "Sens : débit\nMotif : LOYER"];
    private static readonly IReadOnlyList<string> Options = ["Alimentation › Courses", "Logement › Loyer", "Aucune de ces catégories"];

    [Fact]
    public async Task Bank_labels_reach_jev_once_allowed_and_everyone_concerned_allows_it()
    {
        var jev = new FakeJev();
        await using var app = await StartAsync(jev, inputPerMillion: 1000);
        await SetUpAsync(app, kinds: []);
        Guid[] both = [app.AdultA.Value, app.AdultB.Value];
        var request = new LlmDecideRequest(Texts, Options, DataClasses.BankLabels, both);

        Assert.Equal(JobCodes.CloudNotAllowed, Refused(await DecideAsync(app, request)));
        Assert.Equal(HttpStatusCode.OK, (await PutKindsAsync(app, [CloudKinds.BankLabels])).StatusCode);
        await AllowAsync(app, app.AdultA);
        Assert.Equal(JobCodes.OffsiteNotAllowed, Refused(await DecideAsync(app, request)));
        Assert.Equal(JobCodes.CloudNotAllowed, Refused(await DecideAsync(app, request with { DataClass = DataClasses.LocalOnly })));
        Assert.Empty(jev.Texts);

        await AllowAsync(app, app.AdultB);
        var decided = Ok(await DecideAsync(app, request));

        // Only the masked texts and the option names left.
        Assert.Equal(Texts, jev.Texts);
        Assert.All(jev.Options, o => Assert.Equal(Options, o));
        Assert.Equal((Model, "cloud:jev-choice", "jev-choice", "1", "cloud"),
            (decided.Model, decided.Digest, decided.Implementation, decided.Version, decided.Placement));
        Assert.All(decided.Decisions, d => Assert.Equal(1, d.Choice));
        await using var scope = Scope(app);
        var calls = await scope.ServiceProvider.GetRequiredService<AiDbContext>().LlmCalls.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, calls.Count); // every refusal is a line too
        var call = Assert.Single(calls, c => c.Status == "succeeded");
        Assert.Equal(("decide", "decide", "jev-choice", "typesafe", Model, 42), (call.Task, call.Use, call.ProfileId, call.Provider, call.Model, call.TokensIn));
        Assert.Equal(0.042m, call.CostEstimateEur); // 42 tokens at 1 000 € per million
        // Calibrated as any decide model: its implementation, and its build is the profile's.
        Assert.Equal(new LlmDecideImplementation("jev-choice", "1", false),
            await scope.ServiceProvider.GetRequiredService<IDecisions>().ImplementationAsync(Model, TestContext.Current.CancellationToken));
        Assert.Equal("cloud:jev-choice", await scope.ServiceProvider.GetRequiredService<IModelDigests>().DigestAsync("decide", Model, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Self_hosted_never_reaches_the_cloud_model()
    {
        var jev = new FakeJev();
        await using var app = await StartAsync(jev, inputPerMillion: 1000);
        await SetUpAsync(app, kinds: [CloudKinds.BankLabels]);
        await AllowAsync(app, app.AdultA);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/settings/uses/decide", new { mode = "self_hosted" })).StatusCode);

        Assert.NotEqual(JobCodes.CloudNotAllowed, Refused(await DecideAsync(app, new LlmDecideRequest(Texts, Options, DataClasses.BankLabels, [app.AdultA.Value]))));

        Assert.Empty(jev.Texts);
        await using var scope = Scope(app);
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IDecisions>().ImplementationAsync(Model, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Over_the_budget_nothing_is_sent_and_the_call_is_blocked()
    {
        var jev = new FakeJev();
        await using var app = await StartAsync(jev, inputPerMillion: 1_000_000_000);
        await SetUpAsync(app, kinds: [CloudKinds.BankLabels]);
        await AllowAsync(app, app.AdultA);

        Assert.Equal(JobCodes.BudgetBlocked, Refused(await DecideAsync(app, new LlmDecideRequest(Texts, Options, DataClasses.BankLabels, [app.AdultA.Value]))));

        Assert.Empty(jev.Texts);
        await using var scope = Scope(app);
        Assert.Equal("blocked_budget", (await scope.ServiceProvider.GetRequiredService<AiDbContext>().LlmCalls.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Only_an_enabled_decide_profile_model_may_be_chosen()
    {
        await using var app = await StartAsync(new FakeJev(), inputPerMillion: 1000);
        Assert.NotEqual(HttpStatusCode.OK,
            (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/settings/uses/decide/model", new { model = Model })).StatusCode);

        await SetUpAsync(app, kinds: []);
        await using var scope = Scope(app);
        Assert.Equal(Model, await scope.ServiceProvider.GetRequiredService<IDecisions>().ModelAsync(TestContext.Current.CancellationToken));
    }

    private Task<NidApp> StartAsync(FakeJev jev, decimal inputPerMillion)
    {
        var profile = LlmProfileLoader.Read($$$"""
            {"id":"jev-choice","provider":"typesafe","tier":"paid","host":"api.typesafe.ai","model":"{{{Model}}}","capabilities":["decide"],"contextTokens":32768,
             "dataTerms":{"trainsOnData":{"eea_ch_uk":false,"other":false},"humanReview":null,"retentionSwitches":[],
             "termsUrl":"https://typesafe.ai/legal/mca","reviewedOn":"2026-11-27"},
             "pricing":{"inputPerMillion":{{{inputPerMillion}}},"outputPerMillion":0,"currency":"EUR"}}
            """)!;
        return NidApp.StartAsync(fixture, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ILlmProfiles>(new Profiles([profile])));
            services.RemoveAll<ICloudAdapter>();
            services.AddSingleton<ICloudAdapter>(jev);
        });
    }

    /// <summary>The profile enabled (a ceiling, the region, a key), <c>decide</c> on its model, and the allowed kinds.</summary>
    private static async Task SetUpAsync(NidApp app, string[] kinds)
    {
        await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/budget", new { monthlyBudgetEur = "10" });
        await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/cloud/region", new { region = "eea_ch_uk" });
        await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/cloud/keys/typesafe", new { key = "ts-test-0123456789abcdef-WXYZ" });
        Assert.Equal(HttpStatusCode.OK,
            (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/ai/cloud/profiles/jev-choice/enable", new { termsReviewedOn = "2026-11-27" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/settings/uses/decide", new { mode = "self_hosted_then_cloud" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/settings/uses/decide/model", new { model = Model })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutKindsAsync(app, kinds)).StatusCode);
    }

    private static Task<HttpResponseMessage> PutKindsAsync(NidApp app, string[] kinds)
        => app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/settings/allowed-kinds", new { kinds });

    private static async Task AllowAsync(NidApp app, PersonId adult)
        => Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(adult, HttpMethod.Put, "/api/v1/ai/assistant/me/offsite", new { preference = "allow" })).StatusCode);

    private static async Task<Result<LlmDecisions>> DecideAsync(NidApp app, LlmDecideRequest request)
    {
        await using var scope = Scope(app);
        return await scope.ServiceProvider.GetRequiredService<IDecisions>().DecideAsync(request, TestContext.Current.CancellationToken);
    }

    private static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
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

    private sealed class Profiles(IReadOnlyList<LlmProfile> all) : ILlmProfiles
    {
        public IReadOnlyList<LlmProfile> All => all;
    }

    /// <summary>Jev as the gateway sees it: every text and option list it received; each text put in the second option, 42 tokens in all.</summary>
    private sealed class FakeJev : ICloudAdapter
    {
        public List<string> Texts { get; } = [];

        public List<IReadOnlyList<string>> Options { get; } = [];

        public string Provider => "typesafe";

        public LlmDecideImplementation DecideImplementation { get; } = new("jev-choice", "1", false);

        public IAsyncEnumerable<ChatRequestOutput> ChatAsync(LlmProfile profile, string key, ChatRequestInput input, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<CloudDecisions> DecideAsync(LlmProfile profile, string key, IReadOnlyList<string> options, IReadOnlyList<string> texts, CancellationToken ct)
        {
            lock (Texts)
            {
                Texts.AddRange(texts);
                Options.Add(options);
            }
            return Task.FromResult(new CloudDecisions([.. texts.Select(_ => new LlmDecision(1, new Dictionary<int, double> { [0] = Math.Log(0.1), [1] = Math.Log(0.9) }))], 42));
        }
    }
}
