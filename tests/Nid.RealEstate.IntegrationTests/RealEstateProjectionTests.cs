using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using NodaTime.Text;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>Scenario spec §4.2 step 3: the household's properties and loans month by month, read as the dashboard reads them. Today is 2025-10-15.</summary>
public class RealEstateProjectionTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<RealEstateProjection> ProjectAsync(
        NidApp app, PersonId reader, VisibilityScope scope = VisibilityScope.Household, int months = 24, decimal growth = 0m,
        RealEstateEvents? events = null)
    {
        await using var projectionScope = app.Services.CreateAsyncScope();
        projectionScope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(reader);
        var projection = projectionScope.ServiceProvider.GetRequiredService<IRealEstateProjection>();
        var result = events is null
            ? await projection.ProjectAsync(reader, scope, NidApp.GoldenCaseToday, months, growth, default)
            : await projection.ProjectAsync(reader, scope, NidApp.GoldenCaseToday, months, growth, events, default);
        return result.TryGetValue(out var value) ? value : throw new InvalidOperationException();
    }

    private static RealEstateEvents Repaying(LocalDate on, decimal amount, string split, Guid? loan = null, string effect = ProjectionEffects.ReduceTerm)
        => new([new ProjectedRepayment(Guid.NewGuid(), on, amount, split, loan is { } id ? new ItemId(id) : null, effect)], []);

    private static RealEstateEvents Selling(Guid property, LocalDate on, decimal? price, decimal costs)
        => new([], [new ProjectedSale(Guid.NewGuid(), new ItemId(property), on, price, costs)]);

    private static decimal D(JsonElement element, string name) => decimal.Parse(S(element, name)!, CultureInfo.InvariantCulture);

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

    // Projection spec §4.2: the same rule as SimulateLoansQuery at the repayment's date, the recalculated schedules projected.
    [Fact]
    public async Task Repayment_reduce_term_matches_the_simulation()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewAsync(app, Golden(app));
        await NewAsync(app, Short(app)); // 0 %: the highest rate, the golden loan, takes the whole amount

        var events = Repaying(new LocalDate(2025, 11, 15), 5_000m, ProjectionSplits.HighestRate);
        var projection = await ProjectAsync(app, app.AdultA, events: events);

        var simulation = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Loans}/overview/simulations",
            new { scope = "household", on = "2025-11-15", amount = "5000", split = "highest_rate", effect = "reduce_term" }));
        var months = simulation.GetProperty("months").EnumerateArray().ToList();
        for (var k = 0; k <= 24; k++)
            Assert.Equal(D(months[k].GetProperty("after"), "amount"), projection.Months[k].Debt);
        Assert.Equal("2025-12", S(simulation, "paymentMonth"));
        Assert.Equal(D(simulation.GetProperty("monthlyPaymentAfter"), "amount"), projection.Months[2].LoanPayments);
        var outcome = Assert.Single(projection.Repayments);
        Assert.Equal((events.Repayments[0].Event, 1, 5_000m), (outcome.Event, outcome.Month, outcome.Applied));
        Assert.Empty(projection.Sales);
    }

    [Fact]
    public async Task Repayment_reduce_payment_lowers_payments()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await NewAsync(app, Golden(app));

        var before = await ProjectAsync(app, app.AdultA);
        var after = await ProjectAsync(app, app.AdultA,
            events: Repaying(new LocalDate(2025, 11, 15), 20_000m, ProjectionSplits.Chosen, loan, ProjectionEffects.ReducePayment));

        var simulation = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Loans}/overview/simulations",
            new { scope = "household", on = "2025-11-15", amount = "20000", split = "chosen", loanId = loan, effect = "reduce_payment" }));
        Assert.Equal(D(simulation.GetProperty("monthlyPaymentAfter"), "amount"), after.Months[2].LoanPayments);
        Assert.Equal((before.Months[0].LoanPayments, before.Months[1].LoanPayments), (after.Months[0].LoanPayments, after.Months[1].LoanPayments));
        for (var k = 2; k <= 24; k++)
            Assert.True(after.Months[k].LoanPayments < before.Months[k].LoanPayments, $"month {k}");
        Assert.Equal(before.LoanEnds.Single().On, after.LoanEnds.Single().On); // the term is kept
        Assert.Equal(20_000m, Assert.Single(after.Repayments).Applied);
    }

    [Fact]
    public async Task Repayment_above_balance_is_capped_at_read()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await NewAsync(app, Short(app)); // 2 000 left after the October payment, 1 000 due on 2025-11-15

        var projection = await ProjectAsync(app, app.AdultA,
            events: Repaying(new LocalDate(2025, 11, 10), 5_000m, ProjectionSplits.SmallestBalance));

        var outcome = Assert.Single(projection.Repayments);
        Assert.Equal((1, 2_000m), (outcome.Month, outcome.Applied));
        Assert.Equal(2_000m, projection.Months[0].Debt);
        Assert.All(projection.Months.Skip(1), m => Assert.Equal((0m, 0m), (m.Debt, m.LoanPayments)));
        var end = Assert.Single(projection.LoanEnds);
        Assert.Equal((new ItemId(loan), new LocalDate(2025, 11, 10)), (end.Loan, end.On));
    }

    // Projection spec §4.3: the property leaves, its loans are settled by their balance, price − costs − balance comes in.
    [Fact]
    public async Task Sale_settles_loans_and_reports_proceeds()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await ValuedHomeAsync(app);
        var body = Golden(app);
        body["propertyId"] = home;
        var loan = await NewAsync(app, body);

        var events = Selling(home, new LocalDate(2026, 10, 1), 320_000m, 10_000m); // month 12, before its payment of 2026-10-15
        var projection = await ProjectAsync(app, app.AdultA, events: events);

        var balance = (await ScheduleAsync(app, loan)).Last(r => S(r, "dueDate") == "2026-09-15");
        var sale = Assert.Single(projection.Sales);
        Assert.Equal((events.Sales[0].Event, 12), (sale.Event, sale.Month));
        Assert.Equal(310_000m - D(balance, "balance"), sale.Proceeds);
        Assert.Equal(D(balance, "balance"), projection.Months[11].Debt);
        Assert.Equal(300_000m, projection.Months[11].PropertyValue);
        Assert.True(projection.Months[11].LoanPayments > 0m);
        Assert.All(projection.Months.Skip(12), m => Assert.Equal((0m, 0m, 0m), (m.PropertyValue, m.Debt, m.LoanPayments)));
        var end = Assert.Single(projection.LoanEnds);
        Assert.Equal((new ItemId(loan), new LocalDate(2026, 10, 1), new ItemId(home)), (end.Loan, end.On, end.Property));
        Assert.Empty(projection.PropertiesFree); // sold, not freed
    }

    [Fact]
    public async Task Sale_without_value_settles_loans()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app)); // no valuation
        var body = Golden(app);
        body["propertyId"] = home;
        var loan = await NewAsync(app, body);

        var projection = await ProjectAsync(app, app.AdultA, events: Selling(home, new LocalDate(2026, 10, 1), null, 10_000m));

        var balance = D((await ScheduleAsync(app, loan)).Last(r => S(r, "dueDate") == "2026-09-15"), "balance");
        Assert.Equal(-10_000m - balance, Assert.Single(projection.Sales).Proceeds);
        Assert.All(projection.Months.Skip(12), m => Assert.Equal((0m, 0m), (m.Debt, m.LoanPayments)));
    }

    [Fact]
    public async Task Sale_monthly_net_is_costs_minus_rent()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await ValuedHomeAsync(app);
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{home}/recurring-costs",
            new { category = "property_tax", amount = "1200", every = new { count = 1, unit = "year" }, startsOn = "2021-10-15" })); // 100 a month
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{home}/tenancies",
            new { label = "T2", startsOn = "2021-01-01", rentMonthly = "900", chargesMonthly = "50" }));

        var projection = await ProjectAsync(app, app.AdultA, growth: 0.02m, events: Selling(home, new LocalDate(2026, 10, 20), null, 0m));

        var sale = Assert.Single(projection.Sales);
        Assert.Equal(-800m, sale.MonthlyNet);
        Assert.Equal(306_000.00m, Math.Round(sale.Proceeds, 2, MidpointRounding.AwayFromZero)); // its projected value at month 12
    }

    [Fact]
    public async Task None_keeps_the_purchase_projection()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await ValuedHomeAsync(app);
        var body = Golden(app);
        body["propertyId"] = home;
        var loan = await NewAsync(app, body);
        await NewAsync(app, Short(app));

        var none = await ProjectAsync(app, app.AdultA, growth: 0.02m, events: RealEstateEvents.None);
        var purchase = await ProjectAsync(app, app.AdultA, growth: 0.02m);

        Assert.Equal(purchase.Months, none.Months);
        Assert.Equal(purchase.LoanEnds, none.LoanEnds);
        Assert.Equal(purchase.PropertiesFree, none.PropertiesFree);
        Assert.Empty(none.Sales);
        Assert.Empty(none.Repayments);
        // The tranche 1 figures: the schedules' balances and payments, the property grown from 300 000.
        var rows = await ScheduleAsync(app, loan);
        for (var k = 1; k <= 24; k++)
        {
            var month = NidApp.GoldenCaseToday.ToYearMonth().PlusMonths(k);
            var golden = rows.Last(r => LocalDatePattern.Iso.Parse(S(r, "dueDate")!).Value <= month.ToDateInterval().End);
            var shortDebt = k < 3 ? (2 - k) * 1_000m : 0m;
            Assert.Equal(D(golden, "balance") + shortDebt, none.Months[k].Debt);
            Assert.Equal(D(golden, "payment") + (k < 3 ? 1_000m : 0m), none.Months[k].LoanPayments);
        }
        Assert.Equal(306_000.00m, Math.Round(none.Months[12].PropertyValue, 2, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public async Task An_event_on_an_item_the_reader_cannot_see_is_ignored()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewAsync(app, Golden(app));
        var secret = await NewPropertyAsync(app, Home(app, "Secret", "private", app.AdultB), app.AdultB);
        var hidden = Golden(app, name: "Prêt de B", visibility: "private", borrowers: [app.AdultB]);
        hidden["propertyId"] = secret;
        var loan = await NewAsync(app, hidden, app.AdultB);

        var before = await ProjectAsync(app, app.AdultA);
        var after = await ProjectAsync(app, app.AdultA, events: new RealEstateEvents(
            Repaying(new LocalDate(2025, 11, 15), 5_000m, ProjectionSplits.Chosen, loan).Repayments,
            Selling(secret, new LocalDate(2026, 1, 1), 300_000m, 0m).Sales));

        Assert.Equal(before.Months, after.Months);
        Assert.Equal(before.LoanEnds, after.LoanEnds);
        Assert.Empty(after.Repayments);
        Assert.Empty(after.Sales);
    }
}
