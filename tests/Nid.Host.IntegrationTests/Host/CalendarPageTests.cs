using System.Net;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>Issue #239: the calendar page, « À venir » over a month across the modules. Today is 2025-10-15 (<see cref="NidApp.GoldenCaseToday"/>).</summary>
public class CalendarPageTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string October = "/api/v1/calendar?from=2025-10-01&to=2025-10-31";

    private sealed record Event(string On, string Kind, Guid? Id, string Module, string Name, string? Label);
    private sealed record Calendar(string From, string To, List<Event> Events);

    [Fact]
    public async Task The_current_month_lists_its_days_already_past_and_only_what_the_reader_sees_in_the_scope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewYearlyAsync(app, "Shared streaming", app.AdultA, Visibility.Shared);
        await NewYearlyAsync(app, "Alice private gym", app.AdultA, Visibility.Private);
        await NewYearlyAsync(app, "Bob private club", app.AdultB, Visibility.Private);

        var household = await app.GetAsync<Calendar>(app.AdultA, October);
        Assert.Equal(("2025-10-01", "2025-10-31"), (household.From, household.To));
        // Renewed on 5 October, before today: the month is read from its first day.
        var renewal = Assert.Single(household.Events, e => e.Name == "Shared streaming");
        Assert.Equal(("2025-10-05", "subscription_renewal", CalendarModules.Subscriptions), (renewal.On, renewal.Kind, renewal.Module));
        Assert.DoesNotContain(household.Events, e => e.Name is "Alice private gym" or "Bob private club");

        // Moi: one's own private items too, never the other adult's.
        var me = await app.GetAsync<Calendar>(app.AdultA, October + "&scope=me");
        Assert.Contains(me.Events, e => e.Name == "Alice private gym");
        Assert.DoesNotContain(me.Events, e => e.Name == "Bob private club");
        Assert.Contains((await app.GetAsync<Calendar>(app.AdultB, October + "&scope=me")).Events, e => e.Name == "Bob private club");
    }

    [Fact]
    public async Task Only_the_modules_asked_are_listed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewYearlyAsync(app, "Shared streaming", app.AdultA, Visibility.Shared);

        var tax = await app.GetAsync<Calendar>(app.AdultA, October + "&modules=tax");
        Assert.All(tax.Events, e => Assert.Equal(CalendarModules.Tax, e.Module));
        var subscriptions = await app.GetAsync<Calendar>(app.AdultA, October + "&modules=tax,subscriptions");
        Assert.Contains(subscriptions.Events, e => e.Name == "Shared streaming");
    }

    [Theory]
    [InlineData("from=2025-09-30&to=2025-10-31", TokenCodes.CalendarRangeInvalid, "from")] // before the current month
    [InlineData("from=2025-10-01&to=2025-12-03", TokenCodes.CalendarRangeInvalid, "to")] // 63 days
    [InlineData("from=2025-11-01&to=2025-10-31", TokenCodes.CalendarRangeInvalid, "to")]
    [InlineData("from=2025-10-01&to=2025-10-31&modules=tax,banking", TokenCodes.CalendarModulesInvalid, "modules")]
    [InlineData("from=2025-10-01&to=2025-10-31&modules=", TokenCodes.CalendarModulesInvalid, "modules")]
    public async Task A_window_outside_the_rules_or_an_unknown_module_is_refused_on_its_field(string query, string code, string field)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var refused = await LoanApi.RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/calendar?{query}"));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, code, field), refused);
    }

    [Fact]
    public async Task Two_months_apart_is_read()
    {
        await using var app = await NidApp.StartAsync(fixture);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/calendar?from=2025-10-01&to=2025-12-02")).StatusCode);
    }

    /// <summary>« À venir » shows the renewal of a long cycle only: renewed every 5 October.</summary>
    private static async Task NewYearlyAsync(NidApp app, string name, PersonId owner, Visibility visibility)
    {
        var id = await app.NewSubscriptionAsync(name, owner, visibility);
        (await app.RawAsync(owner, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2024-10-05",
            price = new { amount = "12.50", currency = "EUR" },
            every = new { count = 1, unit = "year" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
    }
}
