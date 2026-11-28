using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Infrastructure.Cloud;
using Nid.Ai.Infrastructure.Persistence;
using Nid.Ai.Protocol.Contracts;
using Nid.Core.Contracts;
using Nid.Documents.Application;
using Nid.Documents.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.DocumentApi;

namespace Nid.Ai.IntegrationTests;

/// <summary>#556: with <c>self_hosted_then_cloud</c> and a cloud profile's model for <c>embed</c>, a document's text goes to that
/// provider only when each of its types is in <c>allowed_kinds</c> and each adult concerned chose <c>allow</c>; a payslip and an
/// unfiled document never do; a search's text only with its reader's <c>allow</c>.</summary>
public sealed class CloudEmbedTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Model = "text-embedding-3-small";
    private static readonly Instant At = Instant.FromUtc(2026, 11, 24, 9, 0);

    [Fact]
    public async Task A_document_of_an_allowed_type_is_embedded_by_the_cloud_profile_once_everyone_concerned_allows_it()
    {
        var provider = new FakeProvider();
        await using var app = await StartAsync(provider);
        await SetUpAsync(app, ["invoice"]);
        var household = "targetKind=core.household";
        var invoice = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=f.pdf&{household}&role=invoice");
        await TextAsync(app, invoice, "Facture de la chaudière");
        var contract = await UploadedAsync(app, app.AdultA, [.. Pdf, (byte)'1'], $"fileName=c.pdf&{household}&role=contract");
        await TextAsync(app, contract, "Contrat d'entretien");
        var payslip = await UploadedAsync(app, app.AdultA, [.. Pdf, (byte)'2'], $"fileName=p.pdf&targetKind=core.person&targetId={app.AdultA.Value}&role=payslip");
        await TextAsync(app, payslip, "Bulletin de salaire");
        var unfiled = await UploadedAsync(app, app.AdultA, [.. Pdf, (byte)'3'], "fileName=u.pdf");
        await TextAsync(app, unfiled, "Note sans classement");

        // Only Alice allows it: the household's invoice concerns Bob too.
        await AllowAsync(app, app.AdultA);
        await IndexAsync(app);
        Assert.Empty(provider.Texts);

        await AllowAsync(app, app.AdultB);
        await IndexAsync(app);

        Assert.Equal(["Facture de la chaudière"], provider.Texts);
        await using var scope = Scope(app);
        var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
        var call = await db.LlmCalls.AsNoTracking().SingleAsync(c => c.Status == "succeeded", TestContext.Current.CancellationToken);
        Assert.Equal(("embed", "openai-embed", "openai", Model, 7), (call.Task, call.ProfileId, call.Provider, call.Model, call.TokensIn));
        Assert.True(call.CostEstimateEur > 0);
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<Nid.Documents.Infrastructure.Persistence.DocumentsDbContext>().Database
            .SqlQueryRaw<int>($"select count(*)::int as \"Value\" from documents.chunk_embedding where model = '{Model}'").ToListAsync(TestContext.Current.CancellationToken) is [var n] ? n : -1);
    }

    [Fact]
    public async Task Nothing_is_sent_for_a_type_not_allowed_nor_without_everyones_allow()
    {
        var provider = new FakeProvider();
        await using var app = await StartAsync(provider);
        await SetUpAsync(app, ["statement"]);
        Guid[] both = [app.AdultA.Value, app.AdultB.Value];

        await AllowAsync(app, app.AdultA);
        Assert.Equal(JobCodes.OffsiteNotAllowed, await RefusedAsync(app, new LlmEmbedRequest(["relevé"], DataClasses.Documents, both, Kinds: ["statement"])));
        await AllowAsync(app, app.AdultB);
        Assert.Equal(JobCodes.CloudNotAllowed, await RefusedAsync(app, new LlmEmbedRequest(["facture"], DataClasses.Documents, both, Kinds: ["statement", "invoice"])));
        Assert.Equal(JobCodes.CloudNotAllowed, await RefusedAsync(app, new LlmEmbedRequest(["vide"], DataClasses.Documents, both, Kinds: [])));
        Assert.Equal(JobCodes.CloudNotAllowed, await RefusedAsync(app, new LlmEmbedRequest(["paie"], DataClasses.LocalOnly, both, Kinds: ["statement"])));
        Assert.Empty(provider.Texts);

        await using var scope = Scope(app);
        var embedded = await scope.ServiceProvider.GetRequiredService<IEmbeddings>().EmbedAsync(
            new LlmEmbedRequest(["relevé"], DataClasses.Documents, both, Kinds: ["statement"]), TestContext.Current.CancellationToken);
        Assert.True(embedded.TryGetValue(out var result));
        Assert.Equal((Model, 3), (result!.Model, result.Dimension));
        // #450: a provider versions its model by its name; its build is the profile's.
        Assert.Equal("cloud:openai-embed", await scope.ServiceProvider.GetRequiredService<IModelDigests>().DigestAsync("embed", Model, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_search_reaches_the_cloud_only_with_its_readers_allow_and_answers_by_its_words_until_then()
    {
        var provider = new FakeProvider();
        await using var app = await StartAsync(provider);
        await SetUpAsync(app, []);

        Assert.Equal(MeaningSearchStates.Unavailable, await MeaningAsync(app));
        Assert.Empty(provider.Texts);

        await AllowAsync(app, app.AdultA);
        Assert.Equal(MeaningSearchStates.Used, await MeaningAsync(app));
        Assert.Equal(["chaudière"], provider.Texts);
    }

    [Fact]
    public async Task Only_an_admin_sets_the_allowed_types_among_those_offered_and_every_adult_is_told()
    {
        await using var app = await StartAsync(new FakeProvider());

        Assert.Equal(HttpStatusCode.Forbidden, (await PutKindsAsync(app, app.AdultB, ["invoice"])).StatusCode);
        var payslip = await PutKindsAsync(app, app.AdultA, ["payslip"]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, payslip.StatusCode);
        Assert.Contains(JobCodes.UnknownKind, await payslip.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        var set = await PutKindsAsync(app, app.AdultA, ["contract", "invoice", "contract"]);

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var view = JsonDocument.Parse(await set.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal(["invoice", "contract"], view.GetProperty("allowedKinds").EnumerateArray().Select(k => k.GetString()));
        await using var scope = Scope(app);
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<Nid.Platform.Infrastructure.Persistence.PlatformDbContext>().Notifications
            .AsNoTracking().CountAsync(n => n.Kind == "ai.data_policy_changed", TestContext.Current.CancellationToken)); // one per adult
    }

    [Fact]
    public void The_shipped_embed_profiles_load_and_the_offered_types_are_document_roles()
    {
        var shipped = new LlmProfileLoader(NullLogger<LlmProfileLoader>.Instance).All.Where(p => p.Capabilities.Contains(ModelUses.Embed)).ToList();

        Assert.Equal(["gemini", "mistral", "openai"], shipped.Select(p => p.Provider).Order());
        Assert.All(shipped, p => Assert.Equal(0, p.Pricing!.OutputPerMillion));
        Assert.All(CloudKinds.Documents, k => Assert.Contains(k, DocumentRoles.All));
        Assert.Equal([.. CloudKinds.Documents, DataClasses.BankLabels], CloudKinds.All);
        Assert.DoesNotContain(DocumentRoles.Payslip, CloudKinds.All);
    }

    private Task<NidApp> StartAsync(FakeProvider provider)
    {
        var profile = LlmProfileLoader.Read($$$"""
            {"id":"openai-embed","provider":"openai","tier":"paid","host":"api.openai.com","model":"{{{Model}}}","capabilities":["embed"],"contextTokens":8192,
             "dataTerms":{"trainsOnData":{"eea_ch_uk":false,"other":false},"humanReview":true,"retentionDays":30,"retentionSwitches":[],
             "termsUrl":"https://developers.openai.com/api/docs/guides/your-data","reviewedOn":"2026-11-22"},
             "pricing":{"inputPerMillion":1000,"outputPerMillion":0,"currency":"EUR"}}
            """)!;
        return NidApp.StartAsync(fixture, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ILlmProfiles>(new Profiles([profile])));
            services.RemoveAll<ICloudAdapter>();
            services.AddSingleton<ICloudAdapter>(provider);
        });
    }

    /// <summary>The profile enabled (a ceiling, the region, a key), <c>embed</c> on it, and the allowed types.</summary>
    private static async Task SetUpAsync(NidApp app, string[] kinds)
    {
        await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/budget", new { monthlyBudgetEur = "10" });
        await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/cloud/region", new { region = "eea_ch_uk" });
        await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/cloud/keys/openai", new { key = "sk-test-0123456789abcdef-WXYZ" });
        Assert.Equal(HttpStatusCode.OK,
            (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/ai/cloud/profiles/openai-embed/enable", new { termsReviewedOn = "2026-11-22" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/settings/uses/embed", new { mode = "self_hosted_then_cloud" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/ai/settings/uses/embed/model", new { model = Model })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutKindsAsync(app, app.AdultA, kinds)).StatusCode);
    }

    private static Task<HttpResponseMessage> PutKindsAsync(NidApp app, PersonId actor, string[] kinds)
        => app.RawAsync(actor, HttpMethod.Put, "/api/v1/ai/settings/allowed-kinds", new { kinds });

    private static async Task AllowAsync(NidApp app, PersonId adult)
        => Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(adult, HttpMethod.Put, "/api/v1/ai/assistant/me/offsite", new { preference = "allow" })).StatusCode);

    private static async Task<string> MeaningAsync(NidApp app)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/documents?page=1&q={Uri.EscapeDataString("chaudière")}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("meaningSearch").GetString()!;
    }

    private static async Task<string> RefusedAsync(NidApp app, LlmEmbedRequest request)
    {
        await using var scope = Scope(app);
        var embedded = await scope.ServiceProvider.GetRequiredService<IEmbeddings>().EmbedAsync(request, TestContext.Current.CancellationToken);
        Assert.True(embedded.TryGetFailure(out var failure), "the call went through");
        return failure!.Code;
    }

    private static async Task TextAsync(NidApp app, Guid document, string text)
    {
        await using var scope = Scope(app);
        await scope.ServiceProvider.GetRequiredService<IDocumentTextStore>().SaveAsync(document, [new Nid.Documents.Application.DocumentTextPage(1, text, "text-layer")], At,
            TestContext.Current.CancellationToken);
    }

    private static async Task IndexAsync(NidApp app)
    {
        await using var scope = Scope(app);
        await scope.ServiceProvider.GetRequiredService<DocumentIndexer>().RunOnceAsync(TestContext.Current.CancellationToken);
    }

    private static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    private sealed class Profiles(IReadOnlyList<LlmProfile> all) : ILlmProfiles
    {
        public IReadOnlyList<LlmProfile> All => all;
    }

    /// <summary>The provider's embeddings: every text it received, and a vector of three for each, its first value its length.</summary>
    private sealed class FakeProvider : ICloudAdapter
    {
        public List<string> Texts { get; } = [];

        public string Provider => "openai";

        public IAsyncEnumerable<ChatRequestOutput> ChatAsync(LlmProfile profile, string key, ChatRequestInput input, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<CloudEmbeddings> EmbedAsync(LlmProfile profile, string key, IReadOnlyList<string> texts, CancellationToken ct)
        {
            lock (Texts)
                Texts.AddRange(texts);
            return Task.FromResult(new CloudEmbeddings([.. texts.Select(t => (IReadOnlyList<float>)[t.Length, 1f, 1f])], 7));
        }
    }
}
