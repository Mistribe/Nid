using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Application;
using Nid.RealEstate.Domain;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.Platform.IntegrationTests;

/// <summary>Notifications design §2.3: the real-estate source says what the dashboard, its upcoming events and the properties watch already say.</summary>
public class RealEstateSourceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LocalDate WatchToday = new(2026, 10, 15);

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    /// <summary>The app on 2026-10-15, with 2025-Q2 and 2026-Q2 stored (a revision is due since 2026-09-01).</summary>
    private static async Task<NidApp> StartOnWatchDayAsync(PostgresFixture fixture)
    {
        var app = await NidApp.StartAsync(fixture,
            services => services.AddSingleton<IClock>(new FixedClock(WatchToday.At(new LocalTime(12, 0)).InUtc().ToInstant())));
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<IIrlStore>();
        await store.UpsertAsync(new IrlValue(new IrlQuarter(2025, 2), 146.68m, new LocalDate(2025, 7, 16), new LocalDate(2025, 7, 21), "insee"), default);
        await store.UpsertAsync(new IrlValue(new IrlQuarter(2026, 2), 148.37m, new LocalDate(2026, 7, 12), new LocalDate(2026, 7, 13), "insee"), default);
        return app;
    }

    private static async Task<Guid> PostAsync(NidApp app, PersonId actor, Guid property, string route, object body)
        => await CreatedAsync(await app.RawAsync(actor, HttpMethod.Post, $"{Properties}/{property}/{route}", body));

    private static async Task<IReadOnlyList<Signal>> SignalsAsync(NidApp app, PersonId reader, LocalDate today, int days)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var source = scope.ServiceProvider.GetServices<INotificationSource>().Single(s => s.Name == "realestate");
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Notifier(reader); // as the evaluation does
        var result = await source.SignalsAsync(reader, today, today.PlusDays(days), CancellationToken.None);
        Assert.True(result.TryGetValue(out var signals), result.TryGetFailure(out var f) ? f!.Message : "");
        return signals!;
    }

    private static readonly string[] DatedKinds = ["tenancy_end", "property_cost_due", "loan_phase_end", "loan_end"];

    [Fact]
    public async Task Dated_signals_match_the_dashboard_within_the_horizon()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var today = NidApp.GoldenCaseToday;
        var home = await NewPropertyAsync(app, Home(app));
        var year = new { count = 1, unit = "year" };
        await PostAsync(app, app.AdultA, home, "recurring-costs", new { category = "property_tax", amount = "1200", every = year, startsOn = "2020-10-20" });
        await PostAsync(app, app.AdultA, home, "recurring-costs", new { category = "maintenance", amount = "80", every = year, startsOn = "2020-12-15" }); // today + 61: beyond the dashboard, within 90
        await PostAsync(app, app.AdultA, home, "tenancies", new { label = "T2 meublé", startsOn = "2021-01-01", endsOn = "2025-12-01", rentMonthly = "900" });
        var loan = Short(app, "Prêt travaux");
        loan["phases"] = new[] { Phase(annualRate: "0", payments: 10), Phase(seq: 2, annualRate: "0.01", payments: 2) };
        await NewAsync(app, loan);

        List<(string, Guid, LocalDate)> expected;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var upcoming = await scope.ServiceProvider.GetRequiredService<RealEstateUpcoming>()
                .UpcomingAsync(app.AdultA, VisibilityScope.Me, today, today.PlusDays(90), CancellationToken.None);
            Assert.True(upcoming.TryGetValue(out var events));
            expected = events!.Where(e => DatedKinds.Contains(e.Kind)).Select(e => (e.Kind, e.Item.Value, e.On))
                .OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Item2).ToList();
        }
        var dashboard = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/dashboard?scope=me")).RootElement
            .GetProperty("upcoming").EnumerateArray().Select(e => (e.GetProperty("kind").GetString()!, e.GetProperty("id").GetGuid())).ToList();

        var signals = await SignalsAsync(app, app.AdultA, today, 90);

        Assert.Contains(expected, x => x.Item1 == "loan_end");
        Assert.Contains(expected, x => x.Item1 == "tenancy_end");
        Assert.Equal(2, expected.Count(x => x.Item1 == "property_cost_due"));
        Assert.All(dashboard.Where(d => DatedKinds.Contains(d.Item1)), d => Assert.Contains(expected, x => x.Item1 == d.Item1 && x.Item2 == d.Item2));
        var actual = signals.Where(s => DatedKinds.Contains(s.Kind)).Select(s => (s.Kind, s.ItemIds.Single().Value, s.DueOn!.Value)).OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Item2).ToList();
        Assert.Equal(expected, actual);
        foreach (var s in signals)
            Assert.StartsWith($"{s.Kind}:{s.ItemIds[0].Value:D}:", s.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watch_signals_match_the_watch_list()
    {
        await using var app = await StartOnWatchDayAsync(fixture);
        // Arrears (nothing was ever paid) and a stale estimate.
        var arrears = await NewPropertyAsync(app, Home(app, "Impayés"));
        await PostAsync(app, app.AdultA, arrears, "tenancies", new { label = "T2", startsOn = "2026-01-01", rentMonthly = "800", chargesMonthly = "50" });
        await PostAsync(app, app.AdultA, arrears, "valuations", new { asOf = "2023-06-01", amount = "250000" });
        // A revision due since 2026-09-01.
        var revised = await NewPropertyAsync(app, Home(app, "Indexé"));
        await PostAsync(app, app.AdultA, revised, "tenancies", new
        {
            label = "T3", startsOn = "2025-09-01", rentMonthly = "800", chargesMonthly = "50", deposit = "800", indexed = true, irlReferenceQuarter = 2,
        });

        var overview = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{Properties}/overview?scope=me")).RootElement;
        string[] codes = ["rent_arrears", "revision_due", "revision_lost", "irl_missing", "valuation_stale"];
        var expected = overview.GetProperty("watch").EnumerateArray()
            .Where(w => codes.Contains(w.GetProperty("code").GetString()))
            .Select(w => (w.GetProperty("code").GetString()!, w.GetProperty("propertyId").GetGuid()))
            .OrderBy(x => x.Item1, StringComparer.Ordinal).ThenBy(x => x.Item2).ToList();

        var signals = await SignalsAsync(app, app.AdultA, WatchToday, 90);

        Assert.Contains(expected, x => x.Item1 == "rent_arrears" && x.Item2 == arrears);
        Assert.Contains(expected, x => x.Item1 == "valuation_stale" && x.Item2 == arrears);
        Assert.Contains(expected, x => x.Item1 == "revision_due" && x.Item2 == revised);
        var actual = signals.Where(s => codes.Contains(s.Kind)).Select(s => (s.Kind, s.ItemIds.Single().Value))
            .OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Value).ToList();
        Assert.Equal(expected, actual);
        Assert.All(signals.Where(s => codes.Contains(s.Kind)), s => Assert.Null(s.DueOn));
    }

    [Fact]
    public async Task Arrears_key_carries_the_month()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var property = await NewPropertyAsync(app, Home(app, "Impayés"));
        await PostAsync(app, app.AdultA, property, "tenancies", new { label = "T2", startsOn = "2025-01-01", rentMonthly = "800", chargesMonthly = "50" });

        var signals = await SignalsAsync(app, app.AdultA, NidApp.GoldenCaseToday, 90);

        var arrears = Assert.Single(signals, s => s.Kind == "rent_arrears");
        Assert.Equal($"rent_arrears:{property:D}:2025-10", arrears.Key);
    }

    [Fact]
    public async Task Loan_of_a_private_property_of_the_other_adult_yields_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var secret = await NewPropertyAsync(app, Home(app, "Secret", "private", app.AdultB), app.AdultB);
        var year = new { count = 1, unit = "year" };
        await PostAsync(app, app.AdultB, secret, "recurring-costs", new { category = "property_tax", amount = "1200", every = year, startsOn = "2020-10-20" });
        await PostAsync(app, app.AdultB, secret, "tenancies", new { label = "T2", startsOn = "2021-01-01", endsOn = "2025-12-01", rentMonthly = "900" });
        var loan = Short(app, "Prêt de B");
        loan["visibility"] = "private";
        loan["borrowers"] = new[] { new { personId = app.AdultB.Value, shareBp = 10_000 } };
        await NewAsync(app, loan, app.AdultB);

        Assert.Empty(await SignalsAsync(app, app.AdultA, NidApp.GoldenCaseToday, 90));
    }

    [Fact]
    public async Task No_amount_in_params()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app));
        await PostAsync(app, app.AdultA, home, "recurring-costs",
            new { category = "property_tax", amount = "1200", every = new { count = 1, unit = "year" }, startsOn = "2020-10-20" });
        await PostAsync(app, app.AdultA, home, "tenancies", new { label = "T2 meublé", startsOn = "2021-01-01", endsOn = "2025-12-01", rentMonthly = "900" });

        var signals = await SignalsAsync(app, app.AdultA, NidApp.GoldenCaseToday, 90);

        var cost = Assert.Single(signals, s => s.Kind == "property_cost_due");
        Assert.Equal(new KeyValuePair<string, string>("category", "property_tax"), Assert.Single(cost.Params));
        var end = Assert.Single(signals, s => s.Kind == "tenancy_end");
        Assert.Equal(new KeyValuePair<string, string>("label", "T2 meublé"), Assert.Single(end.Params));
        foreach (var value in signals.SelectMany(s => s.Params.Values))
            Assert.False(decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) && n is 900m or 1200m, value);
        Assert.All(signals.Where(s => s.Kind == "rent_arrears"), s => Assert.Empty(s.Params));
    }

    [Fact]
    public async Task The_kinds_are_declared()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var source = scope.ServiceProvider.GetServices<INotificationSource>().Single(s => s.Name == "realestate");

        Assert.Equal(
        [
            new NotificationKind("tenancy_end", "due_date", NotificationLevel.Action, "properties", 90),
            new NotificationKind("property_cost_due", "due_date", NotificationLevel.Info, "properties", 7),
            new NotificationKind("loan_phase_end", "due_date", NotificationLevel.Info, "loans", 30),
            new NotificationKind("loan_end", "due_date", NotificationLevel.Info, "loans", 30),
            new NotificationKind("rent_arrears", "finding", NotificationLevel.Action, "properties", null),
            new NotificationKind("revision_due", "finding", NotificationLevel.Action, "properties", null),
            new NotificationKind("revision_lost", "finding", NotificationLevel.Action, "properties", null),
            new NotificationKind("irl_missing", "finding", NotificationLevel.Info, "properties", null),
            new NotificationKind("valuation_stale", "finding", NotificationLevel.Info, "properties", null),
        ], source.Kinds);
    }
}
