using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Ai.Protocol.Contracts;
using Nid.Core.Contracts;
using Nid.Host.IntegrationTests.Admin;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>#501: <c>POST /api/v1/ai/assistant/turn</c>, one turn as server-sent events, in the browser only.</summary>
public sealed class AssistantEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Turn = "/api/v1/ai/assistant/turn";
    private static readonly LlmDestination Salon = new(LlmPlacement.Local, "salon", WorkerId: Guid.NewGuid());

    [Fact]
    public async Task A_turn_streams_its_answer_and_links_only_the_items_it_names_never_a_url_the_model_wrote()
    {
        var model = new ScriptedGateway(Salon,
            _ => [new(ToolCall: new ChatToolCall("c1", "list_envelopes", "{}")), new(Usage: new ChatUsage(5, 1, ChatStop.ToolCalls, "llama3.2"))],
            _ => [new(Text: "Votre PEA vaut 21000 EUR, voir https://evil.example/pea."), new(Usage: new ChatUsage(9, 4, ChatStop.End, "llama3.2"))]);
        await using var app = await NidApp.StartAsync(fixture, s => s.Replace(ServiceDescriptor.Scoped<ILlmGateway>(_ => model)));
        var pea = await McpSeed.EnvelopeAsync(app, app.AdultA);
        var named = await McpSeed.IncomeSourceAsync(app, app.AdultA); // returned by no tool of this turn: no link
        await AssistantMe.EnableAsync(app, app.AdultA);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Turn, new { question = "Mon PEA ?" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var events = Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["tool", "text", "done"], events.Select(e => e.Type));
        Assert.Equal("list_envelopes", events[0].Data.GetProperty("tool").GetString());
        var done = events[2].Data;
        Assert.Equal(("salon", "local", "llama3.2", "end"),
            (done.GetProperty("provider").GetString(), done.GetProperty("placement").GetString(), done.GetProperty("model").GetString(), done.GetProperty("reason").GetString()));
        var link = Assert.Single(done.GetProperty("links").EnumerateArray());
        Assert.Equal((pea, "investments", "PEA"), (link.GetProperty("id").GetGuid(), link.GetProperty("module").GetString(), link.GetProperty("name").GetString()));
        Assert.DoesNotContain(named.ToString(), done.GetRawText());
        Assert.Equal([ChatRole.System, ChatRole.User], model.Requests[0].Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task A_refusal_streams_its_code()
    {
        var model = new ScriptedGateway(Salon, _ => []) { Refuses = JobCodes.NoWorker };
        await using var app = await NidApp.StartAsync(fixture, s => s.Replace(ServiceDescriptor.Scoped<ILlmGateway>(_ => model)));
        await AssistantMe.EnableAsync(app, app.AdultA);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Turn, new { question = "Allô ?" });

        var error = Assert.Single(Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(("error", "ai.no_worker"), (error.Type, error.Data.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task A_token_never_asks_the_assistant_and_a_turn_needs_a_question()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var token = await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Direct);
        await AssistantMe.EnableAsync(app, app.AdultA);

        var bearer = await app.BearerAsync(token.Secret, HttpMethod.Post, Turn, new { question = "Mon PEA ?" });
        var empty = await app.RawAsync(app.AdultA, HttpMethod.Post, Turn, new { question = "" });

        Assert.Contains(bearer.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        var problem = await empty.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("#/question", problem);
    }

    private static List<(string Type, JsonElement Data)> Parse(string stream)
        => [.. stream.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(block =>
        {
            var lines = block.Split('\n');
            var type = lines.Single(l => l.StartsWith("event: ", StringComparison.Ordinal))["event: ".Length..];
            var data = string.Concat(lines.Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => l["data: ".Length..]));
            return (type, JsonDocument.Parse(data).RootElement.Clone());
        })];
}
