using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Infrastructure.Cloud;
using NodaTime;

namespace Nid.Ai.Tests;

/// <summary>#812: Jev's <c>choice</c> primitive, answered by a fake System One API: one request per text, the options as its criteria,
/// the probabilities back by option place as log-probabilities, the input tokens summed, and failures worded as every adapter words them.</summary>
public sealed class JevAdapterTests
{
    private const string Host = "api.typesafe.ai";
    private static readonly IReadOnlyList<string> Options = ["Alimentation › Courses", "Logement › Loyer", "Aucune de ces catégories"];

    [Fact]
    public async Task Each_text_is_one_choice_question_and_its_probabilities_come_back_by_place()
    {
        var fake = new FakeSystemOne(HttpStatusCode.OK, """
            {"model":"jev-1.14.0","answers":{"category":{"type":"choice","choice":"Logement › Loyer","confidence":0.8,
             "probabilities":{"Alimentation › Courses":0.1,"Logement › Loyer":0.9,"Aucune de ces catégories":0.0}}},
             "usage":{"input_tokens":40,"output_tokens":3}}
            """);

        var decided = await Adapter(fake).DecideAsync(Profile(), "ts-key-1234", Options, ["Motif : LOYER", "Motif : AGENCE"],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, fake.Bodies.Count);
        Assert.Equal($"https://{Host}/v1/systemone", fake.Uri!.ToString());
        Assert.Equal("Bearer ts-key-1234", fake.Authorization);
        var body = fake.Bodies[0];
        Assert.Equal(["model", "state", "questions"], body.Select(p => p.Key));
        Assert.Equal(("jev-1.14.0", "Motif : LOYER"), (body["model"]!.GetValue<string>(), body["state"]!.GetValue<string>()));
        Assert.Equal("Motif : AGENCE", fake.Bodies[1]["state"]!.GetValue<string>());
        var question = body["questions"]!["category"]!.AsObject();
        Assert.Equal("choice", question["type"]!.GetValue<string>());
        Assert.Equal(JevAdapter.Instructions, question["instructions"]!.GetValue<string>());
        // Only the masked text and the option names leave: each criterion's description is null.
        Assert.Equal(Options, question["criteria"]!.AsObject().Select(c => c.Key));
        Assert.All(question["criteria"]!.AsObject(), c => Assert.Null(c.Value));

        Assert.Equal(80, decided.TokensIn);
        Assert.All(decided.Decisions, d =>
        {
            Assert.Equal(1, d.Choice);
            Assert.Equal([0, 1, 2], d.Scores.Keys.Order());
            Assert.Equal(Math.Log(0.9), d.Scores[1], 9);
            Assert.Equal(Math.Log(1e-9), d.Scores[2], 9); // a probability of 0 at the floor
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "{}", CloudFailure.Transient, JobCodes.CloudFailed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", CloudFailure.Transient, JobCodes.CloudFailed)]
    [InlineData(HttpStatusCode.Unauthorized, "{}", CloudFailure.Refused, CloudCodes.InvalidKey)]
    [InlineData(HttpStatusCode.UnprocessableEntity, """{"detail":[]}""", CloudFailure.Refused, JobCodes.CloudRefused)]
    // A choice that is no option, an option it was not asked, a missing answer or no JSON: refused, never half a batch.
    [InlineData(HttpStatusCode.OK, """{"answers":{"category":{"type":"choice","choice":"Autre","probabilities":{"Autre":1.0}}}}""", CloudFailure.Refused, JobCodes.CloudFailed)]
    [InlineData(HttpStatusCode.OK, """{"answers":{"category":{"type":"choice","choice":"Logement › Loyer","probabilities":{"Logement › Loyer":0.5,"Autre":0.5}}}}""", CloudFailure.Refused, JobCodes.CloudFailed)]
    [InlineData(HttpStatusCode.OK, """{"answers":{}}""", CloudFailure.Refused, JobCodes.CloudFailed)]
    [InlineData(HttpStatusCode.OK, "<html>", CloudFailure.Refused, JobCodes.CloudFailed)]
    public async Task A_failure_is_worded_as_for_any_provider(HttpStatusCode status, string body, CloudFailure kind, string code)
    {
        var failed = await Assert.ThrowsAsync<CloudCallException>(() => Adapter(new FakeSystemOne(status, body))
            .DecideAsync(Profile(), "key", Options, ["Motif : LOYER"], TestContext.Current.CancellationToken));

        Assert.Equal((kind, code), (failed.Kind, failed.Code));
    }

    [Fact]
    public async Task Two_options_of_the_same_name_send_nothing()
    {
        var fake = new FakeSystemOne(HttpStatusCode.OK, "{}");

        var failed = await Assert.ThrowsAsync<CloudCallException>(() => Adapter(fake)
            .DecideAsync(Profile(), "key", ["Courses", "Courses"], ["Motif : LIDL"], TestContext.Current.CancellationToken));

        Assert.Equal((CloudFailure.Refused, JobCodes.InvalidInput), (failed.Kind, failed.Code));
        Assert.Empty(fake.Bodies);
    }

    [Fact]
    public void The_shipped_profile_serves_decide_only_with_its_terms_dated_and_human_review_unstated()
    {
        var jev = Assert.Single(new LlmProfileLoader(NullLogger<LlmProfileLoader>.Instance).All, p => p.Capabilities.Contains(ModelUses.Decide));

        Assert.Equal(("jev-choice", "typesafe", "paid", Host, "jev-1.14.0"), (jev.Id, jev.Provider, jev.Tier, jev.Host, jev.Model));
        Assert.Equal([ModelUses.Decide], jev.Capabilities);
        Assert.DoesNotContain(true, jev.DataTerms.TrainsOnData.Values);
        Assert.Null(jev.DataTerms.HumanReview);
        Assert.Null(jev.DataTerms.RetentionDays);
        Assert.Equal(new LocalDate(2026, 11, 27), jev.DataTerms.ReviewedOn);
        Assert.Equal(new Pricing(0.042m, 0, "USD"), jev.Pricing);
        Assert.Equal(new LlmDecideImplementation("jev-choice", "1", false), new JevAdapter(new CloudHttp(new HttpClient())).DecideImplementation);
    }

    [Fact]
    public void A_profile_whose_human_review_is_left_out_does_not_load()
    {
        Assert.Null(LlmProfileLoader.Read("""
            {"id":"jev","provider":"typesafe","tier":"free","host":"api.typesafe.ai","model":"jev-1.14.0","capabilities":["decide"],"contextTokens":32768,
             "dataTerms":{"trainsOnData":{"eea_ch_uk":false,"other":false},"retentionSwitches":[],"termsUrl":"https://typesafe.ai/legal/mca","reviewedOn":"2026-11-27"}}
            """));
    }

    private static JevAdapter Adapter(FakeSystemOne fake) => new(new CloudHttp(new HttpClient(CloudHttp.Handler([Host], fake))));

    private static LlmProfile Profile()
        => new("jev-choice", "typesafe", "paid", Host, "jev-1.14.0", [ModelUses.Decide],
            new DataTerms(new Dictionary<string, bool> { ["eea_ch_uk"] = false, ["other"] = false }, null, null, [], "https://typesafe.ai/legal/mca",
                new LocalDate(2026, 11, 27)),
            Pricing: new Pricing(0.042m, 0, "USD"), ContextTokens: 32768);

    private sealed class FakeSystemOne(HttpStatusCode status, string answer) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }

        public string? Authorization { get; private set; }

        public List<JsonObject> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());
            return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }
}
