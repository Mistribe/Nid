using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Income.Application;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Notifications;

/// <summary>Notifications design §2.3: the income source says what the dashboard's upcoming events already say (today: 2025-10-15).</summary>
public class IncomeSourceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<ItemId> SourceAsync(NidApp app, string name, string label, string startsOn, string? endsOn, PersonId? owner = null, string visibility = "shared")
    {
        var who = owner ?? app.AdultA;
        var response = await app.RawAsync(who, HttpMethod.Post, "/api/v1/income/sources", new
        {
            displayName = name, kind = "salary", currency = "EUR", visibility,
            owners = new[] { new { personId = who.Value, shareBp = 10_000 } },
            phases = new[]
            {
                new { label, startsOn, endsOn, netAmount = new { amount = "2000", currency = "EUR" }, every = new { count = 1, unit = "month" } },
            },
        });
        response.EnsureSuccessStatusCode();
        return new ItemId(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid());
    }

    private static async Task<IReadOnlyList<Signal>> SignalsAsync(NidApp app, PersonId reader, int days)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var source = scope.ServiceProvider.GetServices<INotificationSource>().Single(s => s.Name == "income");
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Notifier(reader); // as the evaluation does
        var today = NidApp.GoldenCaseToday;
        var result = await source.SignalsAsync(reader, today, today.PlusDays(days), CancellationToken.None);
        Assert.True(result.TryGetValue(out var signals), result.TryGetFailure(out var f) ? f!.Message : "");
        return signals!;
    }

    [Fact]
    public async Task Signals_match_the_upcoming_events_and_the_dashboard()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var today = NidApp.GoldenCaseToday;
        var cdd = await SourceAsync(app, "Employeur", "CDD", "2025-01-01", "2025-11-30");
        await SourceAsync(app, "Lointain", "CDI", "2025-01-01", "2026-06-30");

        List<(string, Guid, LocalDate)> expected;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var upcoming = await scope.ServiceProvider.GetRequiredService<IncomeSourcesUpcoming>()
                .UpcomingAsync(app.AdultA, VisibilityScope.Me, today, today.PlusDays(90), CancellationToken.None);
            Assert.True(upcoming.TryGetValue(out var events));
            expected = events!.Select(e => (e.Kind, e.Item.Value, e.On)).OrderBy(x => x.Item1, StringComparer.Ordinal).ThenBy(x => x.Item2).ToList();
        }
        var dashboard = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/dashboard?scope=me")).RootElement;
        var onDashboard = dashboard.GetProperty("upcoming").EnumerateArray().Concat(dashboard.GetProperty("todo").EnumerateArray())
            .Where(e => e.GetProperty("kind").GetString() is "income_phase_end" or "income_gap_soon")
            .Select(e => (e.GetProperty("kind").GetString()!, e.GetProperty("id").GetGuid())).ToList();

        var signals = await SignalsAsync(app, app.AdultA, 90);

        Assert.Equal(["income_gap_soon", "income_phase_end"], expected.Select(x => x.Item1));
        Assert.NotEmpty(onDashboard);
        Assert.All(onDashboard, d => Assert.Contains(expected, x => x.Item1 == d.Item1 && x.Item2 == d.Item2));
        Assert.All(expected, x => Assert.Equal(cdd.Value, x.Item2));
        var actual = signals.Select(s => (s.Kind, s.ItemIds.Single().Value, s.DueOn!.Value)).OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Item2).ToList();
        Assert.Equal(expected, actual);
        foreach (var s in signals)
            Assert.StartsWith($"{s.Kind}:{s.ItemIds[0].Value:D}:", s.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_adults_private_source_yields_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SourceAsync(app, "Secret", "CDD", "2025-01-01", "2025-11-30", app.AdultB, "private");

        Assert.Empty(await SignalsAsync(app, app.AdultA, 90));
        Assert.NotEmpty(await SignalsAsync(app, app.AdultB, 90));
    }

    [Fact]
    public async Task Params_name_the_phase_and_hold_no_amount()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SourceAsync(app, "Employeur", "CDD", "2025-01-01", "2025-11-30");

        var signals = await SignalsAsync(app, app.AdultA, 90);

        Assert.NotEmpty(signals);
        Assert.All(signals, s => Assert.Equal(new KeyValuePair<string, string>("label", "CDD"), Assert.Single(s.Params)));
        foreach (var value in signals.SelectMany(s => s.Params.Values))
            Assert.False(decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _), value);
    }

    [Fact]
    public async Task The_kinds_are_declared()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var source = scope.ServiceProvider.GetServices<INotificationSource>().Single(s => s.Name == "income");

        Assert.Equal(
        [
            new NotificationKind("income_phase_end", "due_date", NotificationLevel.Action, "income", 30),
            new NotificationKind("income_gap_soon", "due_date", NotificationLevel.Action, "income", 30),
        ], source.Kinds);
    }
}
