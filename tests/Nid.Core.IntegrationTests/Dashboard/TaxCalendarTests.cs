using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Core.IntegrationTests.Dashboard;

/// <summary>Issue #409: the yearly tax calendar in « À venir » and the notifications, on the 2026 dates.</summary>
public class TaxCalendarTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private static Task<NidApp> StartAsync(PostgresFixture fixture, LocalDate today)
        => NidApp.StartAsync(fixture, services => services.AddSingleton<IClock>(new FixedClock(today.At(new LocalTime(12, 0)).InUtc().ToInstant())));

    private static async Task<List<string>> TaxEventsAsync(NidApp app, PersonId reader, string scope = "household")
        => JsonDocument.Parse(await app.GetRawAsync(reader, $"/api/v1/dashboard?scope={scope}")).RootElement.GetProperty("upcoming").EnumerateArray()
            .Where(e => e.GetProperty("kind").GetString()!.Contains("tax_", StringComparison.Ordinal))
            .Select(e => $"{e.GetProperty("on").GetString()} {e.GetProperty("kind").GetString()} {e.GetProperty("label").GetString() ?? "-"} "
                         + (e.GetProperty("amount").ValueKind == JsonValueKind.Null ? "-" : e.GetProperty("amount").GetProperty("amount").GetString()))
            .ToList();

    private static async Task SaveDepartmentAsync(NidApp app, string? department, int year = 2026)
        => Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/tax/profile/{year}?scope=household",
            new { pinel = Array.Empty<object>(), department })).StatusCode);

    private static async Task<List<string>> EventsAsync(NidApp app, LocalDate today, LocalDate until)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Notifier(app.AdultA);
        return (await scope.ServiceProvider.GetRequiredService<Nid.Core.Application.Tax.TaxCalendarEvents>()
                .EventsAsync(app.AdultA, VisibilityScope.Me, today, until, CancellationToken.None))
            .Select(e => $"{e.On:yyyy-MM-dd} {e.Kind}").ToList();
    }

    [Fact]
    public async Task A_window_holding_only_the_households_deadline_has_it()
    {
        var today = new LocalDate(2026, 5, 22); // after the North deadline, before the South one
        await using var app = await StartAsync(fixture, today);
        await SaveDepartmentAsync(app, "33");

        Assert.Equal(["2026-05-28 tax_filing_deadline"], await EventsAsync(app, today, new LocalDate(2026, 5, 30)));
    }

    [Fact]
    public async Task The_may_filing_takes_the_department_of_last_years_revenues()
    {
        var today = new LocalDate(2026, 4, 20);
        await using var app = await StartAsync(fixture, today);
        await SaveDepartmentAsync(app, "33", 2025); // the revenues filed in May 2026
        await SaveDepartmentAsync(app, "75"); // a move this year

        Assert.Equal(["2026-05-28 tax_filing_deadline"], await EventsAsync(app, today, new LocalDate(2026, 6, 30)));
    }

    [Fact]
    public async Task Without_a_department_the_earliest_deadline_comes_with_a_note()
    {
        await using var app = await StartAsync(fixture, new LocalDate(2026, 4, 20));

        Assert.Equal(["2026-05-21 tax_filing_deadline department_unknown -"], await TaxEventsAsync(app, app.AdultA));
    }

    [Theory]
    [InlineData("33", "2026-05-28")]
    [InlineData("75", "2026-06-04")]
    [InlineData("2a", "2026-05-28")]
    [InlineData("13", "2026-05-21")]
    [InlineData("974", "2026-06-04")]
    public async Task The_department_dates_the_filing_deadline(string department, string on)
    {
        await using var app = await StartAsync(fixture, new LocalDate(2026, 4, 20));
        await SaveDepartmentAsync(app, department);

        Assert.Equal([$"{on} tax_filing_deadline - -"], await TaxEventsAsync(app, app.AdultA, "me"));
    }

    [Fact]
    public async Task An_unknown_department_is_refused()
    {
        await using var app = await StartAsync(fixture, new LocalDate(2026, 4, 20));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/tax/profile/2026",
            new { pinel = Array.Empty<object>(), department = "20" })).StatusCode);
    }

    [Fact]
    public async Task The_new_rate_the_balance_of_last_years_notice_and_the_property_tax_follow_in_september_and_october()
    {
        await using var app = await StartAsync(fixture, new LocalDate(2026, 8, 20));
        var notice = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/tax/assessments", new
        {
            year = 2025,
            declarants = new[] { new { personId = app.AdultA.Value, rank = 1 }, new { personId = app.AdultB.Value, rank = 2 } },
            balance = new { amount = "512.00", currency = "EUR" },
        });
        Assert.Equal(HttpStatusCode.Created, notice.StatusCode);
        var home = await PropertyApi.NewPropertyAsync(app, PropertyApi.Home(app));
        await LoanApi.CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{PropertyApi.Properties}/{home}/recurring-costs",
            new { category = "property_tax", amount = "900", every = new { count = 1, unit = "year" }, startsOn = "2020-11-01" }));

        Assert.Equal(
            ["2026-09-01 tax_new_rate - -", "2026-09-25 tax_balance_due - 512.00", "2026-10-15 property_tax_due - -"],
            await TaxEventsAsync(app, app.AdultA));

        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Notifier(app.AdultA);
        var today = new LocalDate(2026, 8, 20);
        var core = await scope.ServiceProvider.GetServices<INotificationSource>().Single(s => s.Name == "core")
            .SignalsAsync(app.AdultA, today, today.PlusDays(60), CancellationToken.None);
        Assert.True(core.TryGetValue(out var signals));
        var tax = signals!.Where(s => s.Kind.StartsWith("tax_", StringComparison.Ordinal)).ToList();
        Assert.Equal(["tax_new_rate:2026-09-01", "tax_balance_due:2026-09-25"], tax.Select(s => s.Key));
        Assert.All(tax, s => Assert.Empty(s.ItemIds));
    }

    [Fact]
    public async Task Without_a_notice_the_balance_date_comes_without_an_amount_and_a_refund_drops_it()
    {
        await using var app = await StartAsync(fixture, new LocalDate(2026, 9, 10));

        Assert.Equal(["2026-09-25 tax_balance_due - -"], await TaxEventsAsync(app, app.AdultA));

        Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/tax/assessments", new
        {
            year = 2025,
            declarants = new[] { new { personId = app.AdultA.Value, rank = 1 }, new { personId = app.AdultB.Value, rank = 2 } },
            balance = new { amount = "-120.00", currency = "EUR" },
        })).StatusCode);
        Assert.Empty(await TaxEventsAsync(app, app.AdultA));
    }

    [Fact]
    public async Task A_lead_time_beyond_the_dashboards_window_still_reaches_the_tax_dates()
    {
        var today = new LocalDate(2026, 3, 1);
        await using var app = await StartAsync(fixture, today);
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Notifier(app.AdultA);

        var core = await scope.ServiceProvider.GetServices<INotificationSource>().Single(s => s.Name == "core")
            .SignalsAsync(app.AdultA, today, today.PlusDays(90), CancellationToken.None);

        Assert.True(core.TryGetValue(out var signals));
        Assert.Equal(["tax_filing_deadline:2026-05-21"], signals!.Where(s => s.Kind.StartsWith("tax_", StringComparison.Ordinal)).Select(s => s.Key));
    }
}
