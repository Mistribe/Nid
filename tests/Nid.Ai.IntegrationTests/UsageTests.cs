using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Nid.Ai.Infrastructure.Persistence;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Ai.IntegrationTests;

/// <summary>#489: « Utilisation IA » shows an adult their own calls, never the other adult's; the month's totals count the
/// whole household.</summary>
public sealed class UsageTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task An_adult_sees_their_own_calls_and_the_households_month()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var now = app.Services.GetRequiredService<IClock>().GetCurrentInstant();
        await SeedAsync(app, app.AdultB.Value, now, "chat", 0.0123m);
        await SeedAsync(app, app.AdultA.Value, now, "chat", 0.5m); // the other adult's private call
        await SeedAsync(app, null, now, "embed", null); // background work
        await SeedAsync(app, app.AdultB.Value, now - Duration.FromDays(62), "chat", 1m); // an earlier month

        var response = await app.RawAsync(app.AdultB, HttpMethod.Get, "/api/v1/ai/usage");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(2, view["total"]!.GetValue<int>());
        Assert.All(view["items"]!.AsArray(), i => Assert.NotEqual("0.50", i!["costEur"]?.GetValue<string>()));
        Assert.Equal("0.0123", view["items"]![0]!["costEur"]!.GetValue<string>());
        Assert.Equal(3, view["totals"]!["calls"]!.GetValue<int>());
        Assert.Equal("0.5123", view["totals"]!["costEur"]!.GetValue<string>());
    }

    private static async Task SeedAsync(NidApp app, Guid? person, Instant at, string use, decimal? cost)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
        db.LlmCalls.Add(new LlmCallRow
        {
            Id = Guid.CreateVersion7(), CreatedAt = at, PersonId = person, Task = "chat", Use = use, Model = "claude-opus-5-5",
            Provider = cost is null ? null : "anthropic", TokensIn = 10, TokensOut = 5, Status = "succeeded", CostEstimateEur = cost,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
