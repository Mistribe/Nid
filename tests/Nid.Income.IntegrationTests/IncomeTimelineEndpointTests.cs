using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Income.IntegrationTests;

/// <summary>Projection spec §3.2, §5.1: the income timeline, read through <see cref="IIncomeContribution"/> as the projection reads it. Today is 2025-10-15.</summary>
public class IncomeTimelineEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Sources = "/api/v1/income/sources";

    private static object Phase(string label, string startsOn, string amount, string? endsOn = null)
        => new { label, startsOn, endsOn, netAmount = new { amount, currency = "EUR" }, every = new { count = 1, unit = "month" } };

    private static async Task<Guid> NewSourceAsync(NidApp app, string name, params object[] phases)
        => await NewKindSourceAsync(app, name, "salary", [new { personId = app.AdultA.Value, shareBp = 10_000 }], phases);

    private static async Task<Guid> NewKindSourceAsync(NidApp app, string name, string kind, object[] owners, params object[] phases)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Sources, new Dictionary<string, object?>
        {
            ["displayName"] = name, ["kind"] = kind, ["currency"] = "EUR", ["visibility"] = "shared",
            ["owners"] = owners,
            ["phases"] = phases,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<(IncomePart Today, IncomeTimeline Timeline)> ReadAsync(NidApp app, int months, IReadOnlyDictionary<PersonId, int>? switches = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
        var sources = scope.ServiceProvider.GetServices<IIncomeContribution>().Single(c => c.GetType().Name == "IncomeSourcesIncome");
        var today = await sources.IncomeAsync(app.AdultA, VisibilityScope.Household, NidApp.GoldenCaseToday, default);
        var timeline = await sources.TimelineAsync(app.AdultA, VisibilityScope.Household, NidApp.GoldenCaseToday, months, switches ?? new Dictionary<PersonId, int>(), default);
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

    private static Dictionary<Currency, decimal> Sum(IEnumerable<TimelinePart> parts)
        => parts.GroupBy(p => p.Currency).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));

    [Fact]
    public async Task Timeline_parts_sum_to_the_monthly_amounts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewSourceAsync(app, "Salaire", Phase("Salaire", "2025-01-01", "2000", "2026-03-01"), Phase("Salaire", "2026-03-01", "2200"), Phase("Prime", "2027-03-01", "300"));

        var (_, timeline) = await ReadAsync(app, 24);

        Assert.NotNull(timeline.Parts);
        Assert.Equal(25, timeline.Parts.Count);
        for (var k = 0; k <= 24; k++)
            Assert.Equal(timeline.Monthly[k].OrderBy(e => e.Key.Code), Sum(timeline.Parts[k]).OrderBy(e => e.Key.Code));
    }

    [Fact]
    public async Task A_phase_running_today_is_indexed_from_zero()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewSourceAsync(app, "Salaire", Phase("Salaire", "2025-01-01", "2000"));

        var (_, timeline) = await ReadAsync(app, 24);

        Assert.All(timeline.Parts!, month => Assert.All(month, p => Assert.Equal(0, p.IndexedFrom)));
    }

    [Fact]
    public async Task A_later_phase_is_indexed_from_its_start_month()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewSourceAsync(app, "Salaire", Phase("Salaire", "2025-01-01", "2000"), Phase("Prime", "2027-03-01", "300"));

        var (_, timeline) = await ReadAsync(app, 24);

        for (var k = 0; k <= 24; k++)
        {
            var prime = timeline.Parts![k].Where(p => p.Amount == 300m).ToList();
            Assert.Equal(k >= 17 ? 1 : 0, prime.Count);
            Assert.All(prime, p => Assert.Equal(17, p.IndexedFrom));
        }
    }

    [Fact]
    public async Task An_extended_phase_is_indexed_from_zero()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewSourceAsync(app, "Mon CDD", Phase("CDD", "2025-01-01", "2000", "2026-03-01"));

        var (_, timeline) = await ReadAsync(app, 24);

        Assert.Single(timeline.Extended);
        var late = timeline.Parts![24];
        Assert.Equal(2000m, Assert.Single(late).Amount);
        Assert.Equal(0, late[0].IndexedFrom);
    }

    [Fact]
    public async Task An_extended_future_phase_keeps_its_start_month()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewSourceAsync(app, "Salaire", Phase("Salaire", "2025-01-01", "2000"), Phase("Mission", "2027-03-01", "500", "2027-06-01"));

        var (_, timeline) = await ReadAsync(app, 24);

        Assert.Single(timeline.Extended);
        var mission = Assert.Single(timeline.Parts![24], p => p.Amount == 500m);
        Assert.Equal(17, mission.IndexedFrom);
    }

    [Fact]
    public async Task A_month_without_income_keeps_its_zero_currency_key()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewSourceAsync(app, "Prime", Phase("Prime", "2027-03-01", "300"));

        var (_, timeline) = await ReadAsync(app, 6);

        Assert.All(timeline.Monthly, m => Assert.Equal(0m, m.Single().Value));
        Assert.Empty(timeline.Parts![0]);
    }

    private static async Task<(IncomeTimeline Timeline, Currency Eur)> RetiringAsync(NidApp app, int? switchA, bool shared = false)
    {
        object[] owners = shared
            ? [new { personId = app.AdultA.Value, shareBp = 5_000 }, new { personId = app.AdultB.Value, shareBp = 5_000 }]
            : [new { personId = app.AdultA.Value, shareBp = 10_000 }];
        await NewKindSourceAsync(app, "Salaire", "salary", owners, Phase("Salaire", "2025-01-01", "2000"));
        await NewKindSourceAsync(app, "Retraite", "pension", [new { personId = app.AdultA.Value, shareBp = 10_000 }], Phase("Retraite", "2035-11-01", "1000"));
        var switches = switchA is { } s ? new Dictionary<PersonId, int> { [app.AdultA] = s } : [];
        var (_, timeline) = await ReadAsync(app, 12, switches);
        return (timeline, Currency.Eur);
    }

    [Fact]
    public async Task Work_income_stops_and_the_pension_starts_at_the_departure()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var (timeline, eur) = await RetiringAsync(app, 3);

        Assert.Equal(2000m, timeline.Monthly[2][eur]);
        Assert.Equal(1000m, timeline.Monthly[3][eur]);
        Assert.Equal(1000m, timeline.Monthly[12][eur]);
        Assert.Empty(timeline.Extended);
        Assert.All(timeline.Parts![3], p => Assert.Equal(0, p.IndexedFrom));
    }

    [Fact]
    public async Task A_departure_next_month_cuts_from_month_one()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var (timeline, eur) = await RetiringAsync(app, 1);

        Assert.Equal(2000m, timeline.Monthly[0][eur]);
        Assert.Equal(1000m, timeline.Monthly[1][eur]);
    }

    [Fact]
    public async Task A_past_departure_changes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var (timeline, eur) = await RetiringAsync(app, 0);

        for (var k = 0; k <= 12; k++)
            Assert.Equal(2000m, timeline.Monthly[k][eur]);
    }

    [Fact]
    public async Task Without_switches_the_timeline_is_unchanged()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var (timeline, eur) = await RetiringAsync(app, null);

        Assert.All(timeline.Monthly, m => Assert.Equal(2000m, m[eur]));
    }

    [Fact]
    public async Task A_salary_shared_with_a_working_adult_is_never_cut()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var (timeline, eur) = await RetiringAsync(app, 3, shared: true);

        Assert.Equal(3000m, timeline.Monthly[12][eur]); // shared salary 2000 + A's pension 1000
    }

    [Fact]
    public async Task A_running_pension_keeps_its_amounts_at_the_departure()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewKindSourceAsync(app, "Retraite", "pension", [new { personId = app.AdultA.Value, shareBp = 10_000 }], Phase("Retraite", "2020-01-01", "1000"));

        var (_, timeline) = await ReadAsync(app, 12, new Dictionary<PersonId, int> { [app.AdultA] = 3 });

        Assert.All(timeline.Monthly, m => Assert.Equal(1000m, m[Currency.Eur]));
        Assert.All(timeline.Parts!, month => Assert.All(month, p => Assert.Equal(0, p.IndexedFrom)));
    }
}
