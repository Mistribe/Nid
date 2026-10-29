using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Platform.IntegrationTests;

/// <summary>Notifications design §2.3: the subscriptions source says what the dashboard and the overview's watch already say.</summary>
public class SubscriptionsSourceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<ItemId> AddAsync(NidApp app, string name, string startsOn, string unit, string? endsOn = null, bool autoRenew = true,
        PersonId? owner = null, Visibility visibility = Visibility.Shared)
    {
        var who = owner ?? app.AdultA;
        var id = await app.NewSubscriptionAsync(name, who, visibility);
        (await app.RawAsync(who, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn, endsOn,
            price = new { amount = "69.90", currency = "EUR" },
            every = new { count = 1, unit },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        if (!autoRenew)
            (await app.RawAsync(who, HttpMethod.Patch, $"/api/v1/subscriptions/{id.Value}", new { autoRenew = false })).EnsureSuccessStatusCode();
        return id;
    }

    private static async Task<IReadOnlyList<Signal>> SignalsAsync(NidApp app, PersonId reader, int days)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var source = scope.ServiceProvider.GetServices<INotificationSource>().Single(s => s.Name == "subscriptions");
        var today = NidApp.GoldenCaseToday;
        var result = await source.SignalsAsync(reader, today, today.PlusDays(days), CancellationToken.None);
        Assert.True(result.TryGetValue(out var signals));
        return signals!;
    }

    [Fact]
    public async Task Signals_match_the_dashboard_and_the_watch()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var renewing = await AddAsync(app, "Amazon Prime", "2024-10-20", "year");
        var ending = await AddAsync(app, "Fin", "2025-01-01", "month", endsOn: "2025-11-30", autoRenew: false);

        var dashboard = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/dashboard?scope=me")).RootElement;
        var overview = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/subscriptions/overview?scope=me")).RootElement;
        var expected = dashboard.GetProperty("upcoming").EnumerateArray()
            .Where(e => e.GetProperty("kind").GetString() is "subscription_renewal" or "subscription_expiry")
            .Select(e => (e.GetProperty("kind").GetString()!, e.GetProperty("id").GetGuid(), e.GetProperty("on").GetString()!))
            .Concat(overview.GetProperty("watch").EnumerateArray()
                .Where(w => w.GetProperty("code").GetString() == "subscriptions.ends_without_renewal")
                .Select(w => ("subscriptions.ends_without_renewal", w.GetProperty("itemIds")[0].GetGuid(), w.GetProperty("date").GetString()!)))
            .OrderBy(x => x.Item1, StringComparer.Ordinal).ThenBy(x => x.Item2).ToList();

        var signals = await SignalsAsync(app, app.AdultA, 60);

        Assert.Contains(expected, x => x.Item1 == "subscription_renewal" && x.Item2 == renewing.Value);
        Assert.Contains(expected, x => x.Item1 == "subscriptions.ends_without_renewal" && x.Item2 == ending.Value);
        var actual = signals
            .Select(s => (s.Kind, s.ItemIds.Single().Value, s.DueOn?.ToString("yyyy-MM-dd", null) ?? s.Key[^10..])) // a finding has no DueOn: its key carries the watch date
            .OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Item2).ToList();
        Assert.Equal(expected, actual);
        foreach (var s in signals)
            Assert.StartsWith($"{s.Kind}:{s.ItemIds[0].Value:D}:", s.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Private_subscription_of_the_other_adult_yields_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await AddAsync(app, "Secret", "2024-10-20", "year", owner: app.AdultB, visibility: Visibility.Private);
        await AddAsync(app, "Secret fin", "2025-01-01", "month", endsOn: "2025-11-30", autoRenew: false, owner: app.AdultB, visibility: Visibility.Private);

        Assert.Empty(await SignalsAsync(app, app.AdultA, 60));
    }

    [Fact]
    public async Task No_amount_in_params()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await AddAsync(app, "Amazon Prime", "2024-10-20", "year");
        await AddAsync(app, "Fin", "2025-01-01", "month", endsOn: "2025-11-30", autoRenew: false);

        var signals = await SignalsAsync(app, app.AdultA, 60);

        Assert.NotEmpty(signals);
        Assert.All(signals, s => Assert.Empty(s.Params));
    }

    [Fact]
    public async Task The_kinds_are_declared()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var source = scope.ServiceProvider.GetServices<INotificationSource>().Single(s => s.Name == "subscriptions");

        Assert.Equal(
        [
            new NotificationKind("subscription_renewal", "due_date", NotificationLevel.Action, "subscriptions", 7),
            new NotificationKind("subscription_expiry", "due_date", NotificationLevel.Action, "subscriptions", 14),
            new NotificationKind("subscriptions.ends_without_renewal", "finding", NotificationLevel.Info, "subscriptions", null),
        ], source.Kinds);
    }
}
