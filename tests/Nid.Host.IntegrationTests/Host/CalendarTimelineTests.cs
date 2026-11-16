using System.Net;
using Microsoft.AspNetCore.Http;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.LoanApi;
using HostingExtensions = Microsoft.Extensions.Hosting.Extensions;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>Issue #240: the calendar's timeline, the milestones from today over 1, 3, 5 or 10 years. Today is 2025-10-15 (<see cref="NidApp.GoldenCaseToday"/>).</summary>
public class CalendarTimelineTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Timeline = "/api/v1/calendar/timeline";

    private sealed record Event(string On, string Kind, Guid? Id, string Module, string Name, string? Label);
    private sealed record Calendar(string From, string To, List<Event> Events);

    [Fact]
    public async Task Ten_years_reach_a_loan_end_and_a_pea_five_years_and_no_recurring_event()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = Golden(app, "Prêt maison");
        loan["totalPayments"] = 168; // monthly from 2020-11-15: the last on 2034-10-15
        await NewAsync(app, loan);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = "PEA Alice", kind = "pea", currency = "EUR", visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            opening = new { occurredOn = "2023-03-01", amount = new { amount = "1000.00", currency = "EUR" } },
        })).EnsureSuccessStatusCode();
        var streaming = await app.NewSubscriptionAsync("Streaming", app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{streaming.Value}/phases", new
        {
            startsOn = "2024-10-20", price = new { amount = "12.50", currency = "EUR" }, every = new { count = 1, unit = "year" }, payer = new { type = "household" },
        })).EnsureSuccessStatusCode();

        var ten = await app.GetAsync<Calendar>(app.AdultA, $"{Timeline}?horizon=10");
        Assert.Equal(("2025-10-15", "2035-10-15"), (ten.From, ten.To));
        Assert.Contains(ten.Events, e => (e.On, e.Kind, e.Name) == ("2034-10-15", UpcomingKinds.LoanEnd, "Prêt maison"));
        Assert.Contains(ten.Events, e => (e.On, e.Kind, e.Label) == ("2028-03-01", UpcomingKinds.TaxMilestone, "pea_5y"));
        Assert.All(ten.Events, e => Assert.True(UpcomingKinds.IsMilestone(e.Kind), e.Kind));
        Assert.DoesNotContain(ten.Events, e => e.Kind == UpcomingKinds.SubscriptionRenewal);

        var five = await app.GetAsync<Calendar>(app.AdultA, $"{Timeline}?horizon=5&modules=loans");
        Assert.Equal("2030-10-15", five.To);
        Assert.All(five.Events, e => Assert.Equal(CalendarModules.Loans, e.Module));
        Assert.DoesNotContain(five.Events, e => e.Kind == UpcomingKinds.LoanEnd);
    }

    [Theory]
    [InlineData("horizon=2", TokenCodes.CalendarRangeInvalid, "horizon")]
    [InlineData("horizon=0", TokenCodes.CalendarRangeInvalid, "horizon")]
    [InlineData("horizon=10&modules=tax,banking", TokenCodes.CalendarModulesInvalid, "modules")]
    public async Task A_horizon_other_than_1_3_5_or_10_years_or_an_unknown_module_is_refused_on_its_field(string query, string code, string field)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var refused = await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Timeline}?{query}"));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, code, field), refused);
    }

    [Theory]
    [InlineData("/api/v1/calendar", true)]
    [InlineData("/api/v1/calendar/timeline", true)]
    [InlineData("/api/v1/calendar/ncal_secret.ics", false)]
    [InlineData("/api/v1/calendar/ncal_secret", false)]
    [InlineData("/api/v1/calendar/timeline/ncal_secret.ics", false)]
    [InlineData("/health", false)]
    public void Traces_keep_the_page_and_its_timeline_never_a_feed_secret(string path, bool traced)
        => Assert.Equal(traced, HostingExtensions.IsTraced(new PathString(path)));
}
