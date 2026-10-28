using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Income;

/// <summary>Projection spec §3.2, §5.1: the income timeline, read through <see cref="IIncomeContribution"/> as the projection reads it. Today is 2025-10-15.</summary>
public class IncomeTimelineEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Sources = "/api/v1/income/sources";

    private static object Phase(string label, string startsOn, string amount, string? endsOn = null)
        => new { label, startsOn, endsOn, netAmount = new { amount, currency = "EUR" }, every = new { count = 1, unit = "month" } };

    private static async Task<Guid> NewSourceAsync(NidApp app, string name, params object[] phases)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Sources, new Dictionary<string, object?>
        {
            ["displayName"] = name, ["kind"] = "salary", ["currency"] = "EUR", ["visibility"] = "shared",
            ["owners"] = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            ["phases"] = phases,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<(IncomePart Today, IncomeTimeline Timeline)> ReadAsync(NidApp app, int months)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
        var sources = scope.ServiceProvider.GetServices<IIncomeContribution>().Single(c => c.GetType().Name == "IncomeSourcesIncome");
        var today = await sources.IncomeAsync(app.AdultA, VisibilityScope.Household, NidApp.GoldenCaseToday, default);
        var timeline = await sources.TimelineAsync(app.AdultA, VisibilityScope.Household, NidApp.GoldenCaseToday, months, default);
        return (today.TryGetValue(out var t) ? t : throw new InvalidOperationException(),
            timeline.TryGetValue(out var l) ? l : throw new InvalidOperationException());
    }

    [Fact]
    public async Task Month_zero_equals_IncomeAsync()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewSourceAsync(app, "Salaire", Phase("Salaire", "2025-01-01", "2000"), Phase("Prime", "2025-01-01", "300"));

        var (today, timeline) = await ReadAsync(app, 12);

        Assert.Equal(13, timeline.Monthly.Count);
        Assert.Equal(today.MonthlyEquivalent, timeline.Monthly[0]);
        Assert.Empty(timeline.Extended);
    }

    [Fact]
    public async Task A_phase_ending_without_successor_is_extended_and_named()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await NewSourceAsync(app, "Mon CDD", Phase("CDD", "2025-01-01", "2000", "2026-03-01"), Phase("Prime", "2026-01-01", "100"));

        var (_, timeline) = await ReadAsync(app, 12);

        var eur = Currency.Eur;
        Assert.Equal(2000m, timeline.Monthly[2][eur]); // 2025-12-31
        Assert.Equal(2100m, timeline.Monthly[3][eur]); // 2026-01-31, the bonus phase has started
        Assert.Equal(2100m, timeline.Monthly[12][eur]); // 2026-10-31, after the CDD end: extended, not lost
        var extended = Assert.Single(timeline.Extended);
        Assert.Equal(new ItemId(source), extended.Source);
        Assert.Equal("Mon CDD", extended.Name);
    }

    [Fact]
    public async Task A_dated_raise_counts_from_its_start()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewSourceAsync(app, "Salaire", Phase("Salaire", "2025-01-01", "2000", "2026-01-01"), Phase("Salaire", "2026-01-01", "2500"));

        var (_, timeline) = await ReadAsync(app, 6);

        Assert.Equal(2000m, timeline.Monthly[2][Currency.Eur]); // 2025-12-31
        Assert.Equal(2500m, timeline.Monthly[3][Currency.Eur]); // 2026-01-31
        Assert.Empty(timeline.Extended);
    }
}
