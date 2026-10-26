using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Notifications;

/// <summary>Notifications design §2.3: the core source says what the dashboard already says about a missing rate and the month's routine (today: 2025-10-15).</summary>
public class CoreSourceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task ValueAsync(NidApp app, PersonId writer, ItemId envelope, string asOf, string amount, string currency = "EUR")
        => Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(writer, HttpMethod.Post,
            $"/api/v1/investments/envelopes/{envelope.Value}/valuations",
            new { asOf, amount = new { amount, currency }, note = (string?)null })).StatusCode);

    private static async Task<IReadOnlyList<Signal>> SignalsAsync(NidApp app, PersonId reader)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var source = scope.ServiceProvider.GetServices<INotificationSource>().Single(s => s.Name == "core");
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Notifier(reader); // as the evaluation does
        var today = NidApp.GoldenCaseToday;
        var result = await source.SignalsAsync(reader, today, today.PlusDays(90), CancellationToken.None);
        Assert.True(result.TryGetValue(out var signals), result.TryGetFailure(out var f) ? f!.Message : "");
        return signals!;
    }

    [Fact]
    public async Task A_stale_value_makes_the_routine_due_for_the_month()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var stale = await app.NewEnvelopeAsync("Livret ancien", app.AdultA, Visibility.Shared);
        await ValueAsync(app, app.AdultA, stale, "2025-07-01", "10.00"); // monthly cadence: stale today
        var dashboard = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/dashboard?scope=me")).RootElement;
        Assert.Contains(dashboard.GetProperty("todo").EnumerateArray(), t => t.GetProperty("kind").GetString() == "stale_value");

        var signal = Assert.Single(await SignalsAsync(app, app.AdultA));

        Assert.Equal("routine_due", signal.Kind);
        Assert.Equal("routine_due:2025-10", signal.Key);
        Assert.Empty(signal.ItemIds);
        Assert.Null(signal.DueOn);
        Assert.Empty(signal.Params);
    }

    [Fact]
    public async Task An_envelope_without_value_counts_too()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewEnvelopeAsync("Livret vide", app.AdultA, Visibility.Shared);

        Assert.Equal("routine_due:2025-10", Assert.Single(await SignalsAsync(app, app.AdultA)).Key);
    }

    [Fact]
    public async Task Nothing_to_update_gives_no_signal()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var fresh = await app.NewEnvelopeAsync("Livret à jour", app.AdultA, Visibility.Shared);
        await ValueAsync(app, app.AdultA, fresh, "2025-10-01", "10.00");

        Assert.Empty(await SignalsAsync(app, app.AdultA));
    }

    [Fact]
    public async Task A_holding_in_a_currency_without_rate_is_a_missing_rate()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var dollars = await app.NewEnvelopeAsync("Dollars", app.AdultA, Visibility.Shared, "USD");
        await ValueAsync(app, app.AdultA, dollars, "2025-10-01", "100.00", "USD"); // the test database holds no rate

        var signal = Assert.Single(await SignalsAsync(app, app.AdultA));

        Assert.Equal("fx_rate_missing", signal.Kind);
        Assert.Equal("fx_rate_missing:USD", signal.Key);
        Assert.Empty(signal.ItemIds);
        Assert.Equal(new KeyValuePair<string, string>("currency", "USD"), Assert.Single(signal.Params));
    }

    [Fact]
    public async Task Another_adults_private_stale_envelope_gives_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var secret = await app.NewEnvelopeAsync("Secret", app.AdultB, Visibility.Private);
        await ValueAsync(app, app.AdultB, secret, "2025-07-01", "10.00");

        Assert.Empty(await SignalsAsync(app, app.AdultA));
        Assert.Equal("routine_due:2025-10", Assert.Single(await SignalsAsync(app, app.AdultB)).Key);
    }

    [Fact]
    public async Task The_kinds_are_declared()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var source = scope.ServiceProvider.GetServices<INotificationSource>().Single(s => s.Name == "core");

        Assert.Equal(
        [
            new NotificationKind("fx_rate_missing", "finding", NotificationLevel.Info, "core", null),
            new NotificationKind("routine_due", "finding", NotificationLevel.Action, "core", null),
        ], source.Kinds);
    }
}
