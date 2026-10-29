using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Investments.Application;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Platform.IntegrationTests;

/// <summary>Notifications design §2.3: the investments source says what the upcoming events and the overview's watch already say (today: 2025-10-15).</summary>
public class InvestmentsSourceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static string Url(ItemId id) => $"/api/v1/investments/envelopes/{id.Value}";

    private static async Task<JsonElement> SendAsync(NidApp app, PersonId who, HttpMethod method, string url, object? body)
    {
        var response = await app.RawAsync(who, method, url, body);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return response.StatusCode == HttpStatusCode.NoContent ? default : JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    /// <summary>Two snapshots whose last gives 55 % of units of account, valued on 2024-12-31 (stale today); floor 70 puts it below.</summary>
    private static async Task<ItemId> BelowFloorAsync(NidApp app, string name, PersonId who, Visibility visibility)
    {
        var id = await app.NewEnvelopeAsync(name, who, visibility);
        var euro = (await SendAsync(app, who, HttpMethod.Post, $"{Url(id)}/holdings", new { name = "Fonds euros", kind = "euro_fund" })).GetProperty("id").GetGuid();
        var fund = (await SendAsync(app, who, HttpMethod.Post, $"{Url(id)}/holdings", new { name = "Fonds A", kind = "fund" })).GetProperty("id").GetGuid();
        await SendAsync(app, who, HttpMethod.Post, $"{Url(id)}/holding-snapshots", new
        {
            asOf = "2023-12-31", recordValuation = true,
            lines = new object[] { new { holdingId = euro, value = Eur("3090.00") }, new { holdingId = fund, units = "99.2", unitPrice = "77", value = Eur("7638.40") } },
        });
        await SendAsync(app, who, HttpMethod.Post, $"{Url(id)}/switches", new
        {
            occurredOn = "2024-06-30", from = new { holdingId = fund, amount = Eur("1600.00"), units = "-20", unitPrice = "80" },
            to = new { holdingId = euro, amount = Eur("1600.00") },
        });
        await SendAsync(app, who, HttpMethod.Post, $"{Url(id)}/holding-snapshots", new
        {
            asOf = "2024-12-31", recordValuation = true,
            lines = new object[] { new { holdingId = euro, value = Eur("4806.70") }, new { holdingId = fund, units = "78.4", unitPrice = "75", value = Eur("5880.00") } },
        });
        await SendAsync(app, who, HttpMethod.Patch, Url(id), new { ucShareFloorPct = 70m });
        return id;
    }

    private static Task AddHoldingAsync(NidApp app, PersonId who, ItemId id, string name, string maturesOn)
        => SendAsync(app, who, HttpMethod.Post, $"{Url(id)}/holdings", new { name, kind = "structured", maturesOn });

    private static async Task<IReadOnlyList<Signal>> SignalsAsync(NidApp app, PersonId reader, int days)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var source = scope.ServiceProvider.GetServices<INotificationSource>().Single(s => s.Name == "investments");
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Notifier(reader); // as the evaluation does
        var today = NidApp.GoldenCaseToday;
        var result = await source.SignalsAsync(reader, today, today.PlusDays(days), CancellationToken.None);
        Assert.True(result.TryGetValue(out var signals), result.TryGetFailure(out var f) ? f!.Message : "");
        return signals!;
    }

    [Fact]
    public async Task Signals_match_the_upcoming_events_and_the_watch()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var today = NidApp.GoldenCaseToday;
        var below = await BelowFloorAsync(app, "Assurance-vie", app.AdultA, Visibility.Shared);
        await AddHoldingAsync(app, app.AdultA, below, "Autocall Europe", "2025-11-20");
        await AddHoldingAsync(app, app.AdultA, below, "Note lointaine", "2026-03-01"); // beyond 90 days
        var stale = await app.NewEnvelopeAsync("Livret ancien", app.AdultA, Visibility.Shared);
        await SendAsync(app, app.AdultA, HttpMethod.Post, $"{Url(stale)}/valuations", new { asOf = "2025-07-01", amount = Eur("10.00") });
        await app.NewEnvelopeAsync("Livret vide", app.AdultA, Visibility.Shared); // no_value: not notified

        List<(string, Guid, LocalDate)> maturities;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var upcoming = await scope.ServiceProvider.GetRequiredService<InvestmentsUpcoming>()
                .UpcomingAsync(app.AdultA, VisibilityScope.Me, today, today.PlusDays(90), CancellationToken.None);
            Assert.True(upcoming.TryGetValue(out var events));
            maturities = events!.Select(e => (e.Kind, e.Item.Value, e.On)).ToList();
        }
        var watch = (await SendAsync(app, app.AdultA, HttpMethod.Get, "/api/v1/investments/overview?scope=me", null)).GetProperty("watch").EnumerateArray()
            .Where(w => w.GetProperty("code").GetString() is "stale_value" or "uc_share_below_floor")
            .Select(w => (w.GetProperty("code").GetString()!, w.GetProperty("envelopeId").GetGuid(), Iso(w.GetProperty("on").GetString()!)))
            .ToList();
        var expected = maturities.Concat(watch).OrderBy(x => x.Item1, StringComparer.Ordinal).ThenBy(x => x.Item2).ToList();

        var signals = await SignalsAsync(app, app.AdultA, 90);

        Assert.Contains(expected, x => x.Item1 == "holding_maturity" && x.Item2 == below.Value && x.Item3 == new LocalDate(2025, 11, 20));
        Assert.Single(expected, x => x.Item1 == "holding_maturity");
        Assert.Contains(expected, x => x.Item1 == "uc_share_below_floor" && x.Item2 == below.Value);
        Assert.Contains(expected, x => x.Item1 == "stale_value" && x.Item2 == stale.Value);
        // A finding has no DueOn: its key carries the watch date.
        var actual = signals.Select(s => (s.Kind, s.ItemIds.Single().Value, s.DueOn ?? Iso(s.Key[^10..])))
            .OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Item2).ToList();
        Assert.Equal(expected, actual);
        foreach (var s in signals)
            Assert.StartsWith($"{s.Kind}:{s.ItemIds[0].Value:D}:", s.Key, StringComparison.Ordinal);
        Assert.All(signals.Where(s => s.Kind != "holding_maturity"), s => Assert.Null(s.DueOn));
        Assert.All(signals.Where(s => s.Kind == "holding_maturity"), s => Assert.NotNull(s.DueOn));
    }

    private static LocalDate Iso(string text) => NodaTime.Text.LocalDatePattern.Iso.Parse(text).Value;

    [Fact]
    public async Task Another_adults_private_envelope_yields_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var secret = await BelowFloorAsync(app, "Secret", app.AdultB, Visibility.Private);
        await AddHoldingAsync(app, app.AdultB, secret, "Autocall secret", "2025-11-20");

        Assert.Empty(await SignalsAsync(app, app.AdultA, 90));
        Assert.NotEmpty(await SignalsAsync(app, app.AdultB, 90));
    }

    [Fact]
    public async Task Params_name_the_holding_and_hold_no_amount()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await BelowFloorAsync(app, "Assurance-vie", app.AdultA, Visibility.Shared);
        await AddHoldingAsync(app, app.AdultA, id, "Autocall Europe", "2025-11-20");

        var signals = await SignalsAsync(app, app.AdultA, 90);

        var maturity = Assert.Single(signals, s => s.Kind == "holding_maturity");
        Assert.Equal(new KeyValuePair<string, string>("label", "Autocall Europe"), Assert.Single(maturity.Params));
        Assert.All(signals.Where(s => s.Kind != "holding_maturity"), s => Assert.Empty(s.Params));
        foreach (var value in signals.SelectMany(s => s.Params.Values))
            Assert.False(decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _), value);
    }

    [Fact]
    public async Task The_kinds_are_declared()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var source = scope.ServiceProvider.GetServices<INotificationSource>().Single(s => s.Name == "investments");

        Assert.Equal(
        [
            new NotificationKind("holding_maturity", "due_date", NotificationLevel.Action, "investments", 30),
            new NotificationKind("stale_value", "finding", NotificationLevel.Info, "investments", null),
            new NotificationKind("uc_share_below_floor", "finding", NotificationLevel.Info, "investments", null),
        ], source.Kinds);
    }
}
