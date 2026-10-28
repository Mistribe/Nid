using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using NodaTime.Text;
using static Nid.Subscriptions.IntegrationTests.RealEstate.LoanApi;
using static Nid.Subscriptions.IntegrationTests.RealEstate.PropertyApi;

namespace Nid.Subscriptions.IntegrationTests.RealEstate;

/// <summary>Scenario spec §4.2 step 3: the household's properties and loans month by month, read as the dashboard reads them. Today is 2025-10-15.</summary>
public class RealEstateProjectionTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<RealEstateProjection> ProjectAsync(
        NidApp app, PersonId reader, VisibilityScope scope = VisibilityScope.Household, int months = 24, decimal growth = 0m)
    {
        await using var projectionScope = app.Services.CreateAsyncScope();
        projectionScope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(reader);
        var result = await projectionScope.ServiceProvider.GetRequiredService<IRealEstateProjection>().ProjectAsync(
            reader, scope, NidApp.GoldenCaseToday, months, growth, default);
        return result.TryGetValue(out var projection) ? projection : throw new InvalidOperationException();
    }

    private static async Task<Guid> ValuedHomeAsync(NidApp app, string amount = "300000")
    {
        var home = await NewPropertyAsync(app, Home(app));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{home}/valuations", new { asOf = "2025-06-01", amount }));
        return home;
    }

    [Fact]
    public async Task Month_zero_matches_the_dashboard() // Review Focus #1
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await ValuedHomeAsync(app);
        var body = Golden(app);
        body["propertyId"] = home;
        await NewAsync(app, body);

        var projection = await ProjectAsync(app, app.AdultA);

        var dashboard = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/dashboard?scope=household")).RootElement
            .GetProperty("netWorth").GetProperty("total").GetProperty("amount").GetString()!;
        Assert.Equal(25, projection.Months.Count);
        Assert.Equal(new YearMonth(2025, 10), projection.Months[0].Month);
        Assert.Equal(decimal.Parse(dashboard, CultureInfo.InvariantCulture), projection.Months[0].PropertyValue - projection.Months[0].Debt);
        Assert.Empty(projection.MissingCurrencies);
    }

    [Fact]
    public async Task A_property_grows_at_the_given_rate()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ValuedHomeAsync(app);

        var projection = await ProjectAsync(app, app.AdultA, growth: 0.02m);

        Assert.Equal(300_000m, projection.Months[0].PropertyValue);
        Assert.Equal(306_000.00m, Math.Round(projection.Months[12].PropertyValue, 2, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public async Task Debt_and_payments_follow_the_schedule()
    {
        await using var app = await NidApp.StartAsync(fixture);
        // 12 000 at 0 % over 12 payments from 2025-05-15, 20 of insurance a month: the last payment is in 2026-04, month 6.
        var body = Short(app);
        body["disbursedOn"] = "2025-05-01";
        body["firstPaymentDate"] = "2025-05-15";
        body["insurances"] = new[] { new { mode = "fixed_monthly", monthlyAmount = "20", startsOn = "2025-05-15" } };
        var loan = await NewAsync(app, body);

        var projection = await ProjectAsync(app, app.AdultA);

        var rows = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Loans}/{loan}/schedule"))).GetProperty("rows");
        var november = rows.EnumerateArray().Single(r => S(r, "dueDate") == "2025-11-15");
        var expected = decimal.Parse(S(november, "payment")!, CultureInfo.InvariantCulture) + decimal.Parse(S(november, "insurance")!, CultureInfo.InvariantCulture);
        Assert.Equal(1_020m, expected);
        Assert.Equal(expected, projection.Months[1].LoanPayments);
        Assert.Equal(6_000m, projection.Months[0].Debt); // six payments made, the October one included
        Assert.Equal((0m, 1_020m), (projection.Months[6].Debt, projection.Months[6].LoanPayments));
        Assert.All(projection.Months.Skip(7), m => Assert.Equal((0m, 0m), (m.Debt, m.LoanPayments)));
    }

    [Fact]
    public async Task The_other_adults_private_property_is_not_counted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var secret = await NewPropertyAsync(app, Home(app, "Secret", "private", app.AdultB), app.AdultB);
        await CreatedAsync(await app.RawAsync(app.AdultB, HttpMethod.Post, $"{Properties}/{secret}/valuations", new { asOf = "2025-06-01", amount = "300000" }));

        Assert.All((await ProjectAsync(app, app.AdultA)).Months, m => Assert.Equal(0m, m.PropertyValue));
        // A private item is in its owner's « Moi » only, as the rents.
        Assert.Equal(300_000m, (await ProjectAsync(app, app.AdultB, VisibilityScope.Me)).Months[0].PropertyValue);
    }

    private static async Task<List<JsonElement>> ScheduleAsync(NidApp app, Guid loan)
        => (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Loans}/{loan}/schedule"))).GetProperty("rows").EnumerateArray().ToList();

    [Fact]
    public async Task Loan_end_is_the_last_schedule_row() // projection spec §3.5, §5.2
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await NewAsync(app, Golden(app));

        var projection = await ProjectAsync(app, app.AdultA);

        var end = Assert.Single(projection.LoanEnds);
        var last = (await ScheduleAsync(app, loan))[^1];
        Assert.Equal(new ItemId(loan), end.Loan);
        Assert.Equal("Prêt maison", end.Name);
        Assert.Equal(LocalDatePattern.Iso.Parse(S(last, "dueDate")!).Value, end.On);
        Assert.Equal(new LocalDate(2040, 10, 15), end.On); // 240 payments from 2020-11-15
        Assert.Null(end.Property);
    }

    [Fact]
    public async Task Property_free_when_its_last_loan_ends()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await ValuedHomeAsync(app);
        var works = Short(app); // last payment 2025-12-15
        works["propertyId"] = home;
        await NewAsync(app, works);
        var longer = Short(app, "Prêt relais"); // 24 payments: the last one on 2026-12-15
        longer["totalPayments"] = 24;
        longer["propertyId"] = home;
        await NewAsync(app, longer);

        var projection = await ProjectAsync(app, app.AdultA, months: 24);

        Assert.Equal(2, projection.LoanEnds.Count);
        Assert.All(projection.LoanEnds, e => Assert.Equal(new ItemId(home), e.Property));
        var free = Assert.Single(projection.PropertiesFree);
        Assert.Equal((new ItemId(home), "Maison", new LocalDate(2026, 12, 15)), (free.Property, free.Name, free.On));
        // A loan still running past the projection's last month leaves the property owing.
        Assert.Empty((await ProjectAsync(app, app.AdultA, months: 12)).PropertiesFree);
    }

    [Fact]
    public async Task Debt_matches_the_schedule_every_month()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await NewAsync(app, Golden(app));

        var projection = await ProjectAsync(app, app.AdultA);

        var rows = (await ScheduleAsync(app, loan))
            .Select(r => (Due: LocalDatePattern.Iso.Parse(S(r, "dueDate")!).Value,
                Balance: decimal.Parse(S(r, "balance")!, CultureInfo.InvariantCulture)))
            .ToList();
        for (var k = 1; k <= 24; k++)
        {
            var end = NidApp.GoldenCaseToday.ToYearMonth().PlusMonths(k).ToDateInterval().End;
            Assert.Equal(rows.Last(r => r.Due <= end).Balance, projection.Months[k].Debt);
        }
    }
}
