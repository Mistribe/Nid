using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Ai.Application;
using Nid.Ai.Infrastructure.Persistence;
using Nid.Ai.Protocol.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Host.IntegrationTests.Mcp;

/// <summary>#175 (<c>docs/ai/README.md</c> §10.3): a conversation's tokens and estimated cost, from the gateway's records; a
/// worker's conversation shows tokens and no cost; a budget block says when paid calls come back.</summary>
public sealed class AssistantCostTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Conversations = "/api/v1/ai/assistant/conversations";
    private static readonly LlmDestination Salon = new(LlmPlacement.Local, "salon", WorkerId: Guid.NewGuid());

    [Fact]
    public async Task A_conversation_sums_the_tokens_and_cost_of_its_calls_and_a_workers_shows_no_cost()
    {
        var model = new ScriptedGateway(Salon, _ => [new(Text: "Bonjour."), new(Usage: new ChatUsage(9, 4, ChatStop.End, "llama3.2"))]);
        await using var app = await NidApp.StartAsync(fixture, s => s.Replace(ServiceDescriptor.Scoped<ILlmGateway>(_ => model)));
        await AssistantMe.EnableAsync(app, app.AdultA);
        var paid = await AskAsync(app, "Un ?");
        var free = await AskAsync(app, "Deux ?");
        // The gateway's records, as a paid cloud call and a worker's call leave them (the scripted gateway records none).
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<IClock>().GetCurrentInstant();
            db.LlmCalls.AddRange(
                Call(paid, now, 1200, 300, 0.0108m), Call(paid, now, 800, 200, 0.0072m), Call(free, now, 500, 100, null),
                Call(Guid.NewGuid(), now, 99_999, 99_999, 9m)); // another conversation's call counts nowhere here
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var one = await GetAsync(app, $"{Conversations}/{paid}");
        var other = await GetAsync(app, $"{Conversations}/{free}");
        var list = await GetAsync(app, Conversations);

        Assert.Equal((2000, 500, "0.018"), (one.GetProperty("tokensIn").GetInt32(), one.GetProperty("tokensOut").GetInt32(), one.GetProperty("costEur").GetString()));
        Assert.Equal((500, 100, JsonValueKind.Null), (other.GetProperty("tokensIn").GetInt32(), other.GetProperty("tokensOut").GetInt32(), other.GetProperty("costEur").ValueKind));
        Assert.Equal("0.018", list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == paid).GetProperty("costEur").GetString());
    }

    [Fact]
    public async Task A_budget_block_says_when_paid_calls_come_back()
    {
        var model = new ScriptedGateway(Salon, _ => throw new ModelCallException(JobCodes.BudgetBlocked));
        await using var app = await NidApp.StartAsync(fixture, s => s.Replace(ServiceDescriptor.Scoped<ILlmGateway>(_ => model)));
        await AssistantMe.EnableAsync(app, app.AdultA);
        LocalDate today;
        await using (var scope = app.Services.CreateAsyncScope())
            today = scope.ServiceProvider.GetRequiredService<IClock>().GetCurrentInstant().InUtc().Date;

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/ai/assistant/turn", new { question = "Allô ?" });

        var error = JsonDocument.Parse(Data(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), "error")).RootElement;
        Assert.Equal("ai.budget_blocked", error.GetProperty("code").GetString());
        Assert.Equal(today.With(DateAdjusters.StartOfMonth).PlusMonths(1).ToString("yyyy-MM-dd", null), error.GetProperty("availableOn").GetString());
    }

    private static LlmCallRow Call(Guid conversation, Instant at, int tokensIn, int tokensOut, decimal? cost) => new()
    {
        Id = Guid.CreateVersion7(), CreatedAt = at, Task = "chat", Use = "chat", ConversationId = conversation, TokensIn = tokensIn, TokensOut = tokensOut,
        Status = "succeeded", CostEstimateEur = cost,
    };

    private static async Task<Guid> AskAsync(NidApp app, string question)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/ai/assistant/turn", new { question });
        return JsonDocument.Parse(Data(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), "done")).RootElement.GetProperty("conversationId").GetGuid();
    }

    private static string Data(string stream, string type)
    {
        var block = stream.Split("\n\n").Single(b => b.StartsWith($"event: {type}", StringComparison.Ordinal));
        return block[(block.IndexOf("data: ", StringComparison.Ordinal) + 6)..];
    }

    private static async Task<JsonElement> GetAsync(NidApp app, string url)
        => await (await app.RawAsync(app.AdultA, HttpMethod.Get, url)).Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
