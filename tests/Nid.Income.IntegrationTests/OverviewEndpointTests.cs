using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.PropertyApi;

namespace Nid.Income.IntegrationTests;

/// <summary>Income dashboard spec §4: the household overview, salaries and rents together. Today is 2025-10-15.</summary>
public class OverviewEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Overview = "/api/v1/income/overview";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<Guid> CreatedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> SalaryAsync(NidApp app, PersonId owner, string name = "Salaire", string visibility = "shared",
        string currency = "EUR", string startsOn = "2024-01-01", string amount = "2000")
        => await CreatedAsync(await app.RawAsync(owner, HttpMethod.Post, "/api/v1/income/sources", new Dictionary<string, object?>
        {
            ["displayName"] = name, ["kind"] = "salary", ["currency"] = currency, ["visibility"] = visibility,
            ["owners"] = new[] { new { personId = owner.Value, shareBp = 10_000 } },
            ["phases"] = new[] { new { label = "Salaire", startsOn, netAmount = new { amount, currency }, every = new { count = 1, unit = "month" } } },
        }));

    private static async Task<Guid> RentalAsync(NidApp app, params PersonId[] owners)
    {
        var property = await NewPropertyAsync(app, Home(app, "Studio", "shared", owners));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/tenancies",
            new { label = "Bail", startsOn = "2024-01-01", rentMonthly = "800", chargesMonthly = "60" }));
        return property;
    }

    private static async Task<JsonElement> OverviewAsync(NidApp app, string query = "", PersonId? reader = null)
    {
        var response = await app.RawAsync(reader ?? app.AdultA, HttpMethod.Get, $"{Overview}{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static string? Amount(JsonElement view) => view.ValueKind == JsonValueKind.Null ? null : view.GetProperty("amount").GetString();

    private static List<JsonElement> Months(JsonElement overview) => overview.GetProperty("months").EnumerateArray().ToList();

    private static List<JsonElement> Rows(JsonElement overview) => overview.GetProperty("sources").EnumerateArray().ToList();

    [Fact]
    public async Task Overview_gives_the_year_month_by_month_with_last_year_behind()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SalaryAsync(app, app.AdultA);

        var overview = await OverviewAsync(app);

        Assert.Equal(("year", "2025-01-01", "2025-12-31", "EUR"), (overview.GetProperty("period").GetString(),
            overview.GetProperty("from").GetString(), overview.GetProperty("to").GetString(), overview.GetProperty("currency").GetString()));
        var months = Months(overview);
        Assert.Equal(12, months.Count);
        Assert.Equal("2000.00", Amount(months[9].GetProperty("total")));
        Assert.Equal("2000.00", Amount(months[9].GetProperty("previousYear")));
        Assert.False(months[9].GetProperty("upcoming").GetBoolean());
        Assert.True(months[10].GetProperty("upcoming").GetBoolean());
        Assert.Equal("Salaire", Assert.Single(months[9].GetProperty("bySource").EnumerateArray()).GetProperty("name").GetString());
        var figures = overview.GetProperty("figures");
        Assert.Equal("20000.00", Amount(figures.GetProperty("elapsed")));
        Assert.Equal("24000.00", Amount(figures.GetProperty("total")));
        Assert.Equal("2000.00", Amount(figures.GetProperty("monthlyNow")));
        Assert.Equal(["salary"], overview.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()));
        var row = Assert.Single(Rows(overview));
        Assert.Equal(("income", "2025-11-01", "2000.00"),
            (row.GetProperty("module").GetString(), row.GetProperty("nextOn").GetString(), Amount(row.GetProperty("nextAmount"))));
    }

    [Fact]
    public async Task Rents_count_without_charges_next_to_the_salaries()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SalaryAsync(app, app.AdultA);
        var rental = await RentalAsync(app, app.AdultA);

        var overview = await OverviewAsync(app);

        Assert.Equal("2800.00", Amount(Months(overview)[9].GetProperty("total")));
        var row = Rows(overview).Single(r => r.GetProperty("id").GetGuid() == rental);
        Assert.Equal(("realestate", "rent", "800.00"),
            (row.GetProperty("module").GetString(), row.GetProperty("kind").GetString(), Amount(row.GetProperty("perMonth"))));
        var share = overview.GetProperty("split").EnumerateArray().Single(s => s.GetProperty("kind").GetString() == "rent");
        Assert.Equal(rental, share.GetProperty("item").GetGuid());
        Assert.Equal("9600.00", Amount(share.GetProperty("total")));
        Assert.Equal(["rent", "salary"], overview.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()));
        var rentsOnly = await OverviewAsync(app, "?kind=rent");
        Assert.Equal("800.00", Amount(Months(rentsOnly)[9].GetProperty("total")));
        Assert.Equal(["rent", "salary"], rentsOnly.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()));
    }

    [Fact]
    public async Task A_person_sees_her_share_of_a_jointly_owned_rental()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RentalAsync(app, app.AdultA, app.AdultB);
        await SalaryAsync(app, app.AdultB, "Salaire B");

        var overview = await OverviewAsync(app, $"?person={app.AdultA.Value}");

        var row = Assert.Single(Rows(overview));
        Assert.Equal("400.00", Amount(row.GetProperty("perMonth")));
        Assert.Equal("400.00", Amount(Months(overview)[9].GetProperty("total")));
    }

    [Fact]
    public async Task An_ended_source_stays_in_last_years_months_but_not_in_the_rows()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var ended = await SalaryAsync(app, app.AdultA, "Ancien", startsOn: "2025-01-01", amount: "1000");
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/income/sources/{ended}/close",
            new { endedOn = "2025-03-31" })).StatusCode);

        var overview = await OverviewAsync(app, "?period=year");

        Assert.Equal("1000.00", Amount(Months(overview)[0].GetProperty("total")));
        Assert.Equal("0.00", Amount(Months(overview)[9].GetProperty("total")));
        Assert.Empty(Rows(overview));
        Assert.Empty(overview.GetProperty("nowSplit").EnumerateArray());
        var row = Assert.Single(Rows(await OverviewAsync(app, "?period=year&ended=true")));
        Assert.Equal(ended, row.GetProperty("id").GetGuid());
        Assert.True(row.GetProperty("ended").GetBoolean());
    }

    [Fact]
    public async Task An_ended_lease_stays_in_last_years_months_but_not_in_the_rows()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var property = await NewPropertyAsync(app, Home(app, "Studio", "shared", app.AdultA));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/tenancies",
            new { label = "Bail", startsOn = "2025-01-01", endsOn = "2025-04-01", rentMonthly = "800", chargesMonthly = "60" }));

        var overview = await OverviewAsync(app, "?period=year");

        Assert.Equal("800.00", Amount(Months(overview)[0].GetProperty("total")));
        Assert.Equal("0.00", Amount(Months(overview)[9].GetProperty("total")));
        Assert.Empty(Rows(overview));
        Assert.Empty(overview.GetProperty("nowSplit").EnumerateArray());
        var row = Assert.Single(Rows(await OverviewAsync(app, "?period=year&ended=true")));
        Assert.Equal(property, row.GetProperty("id").GetGuid());
        Assert.True(row.GetProperty("ended").GetBoolean());
    }

    [Fact]
    public async Task A_month_without_a_rate_has_no_total_and_names_the_currency()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SalaryAsync(app, app.AdultA, "Dollars", currency: "USD");

        var overview = await OverviewAsync(app);

        Assert.Null(Amount(Months(overview)[9].GetProperty("total")));
        Assert.Null(Amount(overview.GetProperty("figures").GetProperty("total")));
        Assert.Null(Amount(Assert.Single(Rows(overview)).GetProperty("perMonth")));
        Assert.Empty(overview.GetProperty("split").EnumerateArray());
        Assert.Equal(["USD"], overview.GetProperty("missingRates").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task Rates_are_read_back_to_the_monthly_amount_three_years_ago()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SalaryAsync(app, app.AdultA, "Dollars", currency: "USD", startsOn: "2022-01-01");
        var days = Enumerable.Range(0, 2000).Select(i => new LocalDate(2022, 1, 1).PlusDays(i)).TakeWhile(d => d <= NidApp.GoldenCaseToday);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IFxRateStore>()
                .WriteAsync(days.Select(d => new FxRate(d, Currency.Eur, 0.9m, FxRateSource.Ecb)).ToList(), TestContext.Current.CancellationToken); // 1 USD = 0.90 EUR

        var overview = await OverviewAsync(app, "?period=year");

        Assert.Empty(overview.GetProperty("missingRates").EnumerateArray());
        var figures = overview.GetProperty("figures");
        Assert.Equal("1800.00", Amount(figures.GetProperty("monthlyNow")));
        Assert.Equal(0m, figures.GetProperty("change3y").GetDecimal());
        Assert.Equal(0m, figures.GetProperty("monthlyChange1y").GetDecimal());
    }

    private static async Task RaiseAsync(NidApp app, Guid id)
        => await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/income/sources/{id}/phase-changes",
            new { label = "Salaire", effectiveOn = "2025-06-01", netAmount = new { amount = "2080", currency = "EUR" } }));

    [Fact]
    public async Task Income_overview_real_change_uses_the_observed_series()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedCpiAsync(new NodaTime.YearMonth(2025, 9), 2m);
        await RaiseAsync(app, await SalaryAsync(app, app.AdultA));

        var figures = (await OverviewAsync(app, "?period=year")).GetProperty("figures");

        var real = figures.GetProperty("monthlyRealChange1y"); // 1.04 / 1.02 − 1 = 1.96 %
        Assert.Equal((4.0m, 2.0m, "2025-09", JsonValueKind.Null, 2.0m), (figures.GetProperty("monthlyChange1y").GetDecimal(),
            real.GetProperty("percent").GetDecimal(), real.GetProperty("inflationTo").GetString(), real.GetProperty("missing").ValueKind,
            figures.GetProperty("inflation").GetDecimal()));
    }

    [Fact]
    public async Task Income_overview_in_a_foreign_reference_currency_has_no_real_change()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedCpiAsync(new YearMonth(2025, 9), 2m);
        await RaiseAsync(app, await SalaryAsync(app, app.AdultA));
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/admin/household", new { referenceCurrency = "USD" })).EnsureSuccessStatusCode();
        var days = Enumerable.Range(0, 2000).Select(i => new LocalDate(2024, 1, 1).PlusDays(i)).TakeWhile(d => d <= NidApp.GoldenCaseToday);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IFxRateStore>()
                .WriteAsync(days.Select(d => new FxRate(d, Currency.Eur, 1.1m, FxRateSource.Ecb)).ToList(), TestContext.Current.CancellationToken);

        var real = (await OverviewAsync(app, "?period=year")).GetProperty("figures").GetProperty("monthlyRealChange1y");

        Assert.Equal("currency", real.GetProperty("missing").GetString());
    }

    [Fact]
    public async Task Income_overview_without_series_keeps_nominal_figures()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RaiseAsync(app, await SalaryAsync(app, app.AdultA));

        var figures = (await OverviewAsync(app, "?period=year")).GetProperty("figures");

        var real = figures.GetProperty("monthlyRealChange1y");
        Assert.Equal((4.0m, JsonValueKind.Null, JsonValueKind.Null, "unavailable", JsonValueKind.Null), (figures.GetProperty("monthlyChange1y").GetDecimal(),
            real.GetProperty("percent").ValueKind, real.GetProperty("inflationTo").ValueKind, real.GetProperty("missing").GetString(),
            figures.GetProperty("inflation").ValueKind));
    }

    [Fact]
    public async Task The_other_adults_private_source_is_never_counted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SalaryAsync(app, app.AdultA);
        await SalaryAsync(app, app.AdultB, "Secret", "private", amount: "5000");

        foreach (var (query, total) in new[] { ("", "2000.00"), ("?scope=me", "2000.00"), ($"?scope=me&person={app.AdultB.Value}", "0.00") })
        {
            var overview = await OverviewAsync(app, query);
            Assert.DoesNotContain(Rows(overview), r => r.GetProperty("name").GetString() == "Secret");
            Assert.Equal(total, Amount(Months(overview)[9].GetProperty("total")));
        }
        Assert.Equal("7000.00", Amount(Months(await OverviewAsync(app, "?scope=me", app.AdultB))[9].GetProperty("total")));
    }

    [Fact]
    public async Task An_unknown_kind_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Overview}?kind=lottery");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("income.unknown_kind", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Overview}?kind=rent")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_period_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Overview}?period=5y");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("income.invalid_period", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(36, Months(await OverviewAsync(app, "?period=3y")).Count);
        Assert.Equal("2024-11-01", (await OverviewAsync(app, "?period=12m")).GetProperty("from").GetString());
    }
    private static async Task<IncomeReviewPart> ReviewAsync(NidApp app, int year)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
        var end = year == NidApp.GoldenCaseToday.Year ? NidApp.GoldenCaseToday : new LocalDate(year, 12, 31);
        var review = await scope.ServiceProvider.GetRequiredService<IIncomeReview>()
            .ReviewAsync(app.AdultA, VisibilityScope.Me, new LocalDate(year - 1, 12, 31), end, TestContext.Current.CancellationToken);
        Assert.True(review.TryGetValue(out var part));
        return part!;
    }

    [Fact]
    public async Task The_overview_reads_a_past_year()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SalaryAsync(app, app.AdultA, startsOn: "2023-06-01");

        var overview = await OverviewAsync(app, "?period=year&year=2024");

        Assert.Equal(("2024-01-01", "2024-12-31"), (overview.GetProperty("from").GetString(), overview.GetProperty("to").GetString()));
        var figures = overview.GetProperty("figures");
        Assert.Equal(("24000.00", "24000.00", "10000.00"),
            (Amount(figures.GetProperty("total")), Amount(figures.GetProperty("elapsed")), Amount(figures.GetProperty("changeVsPrevious"))));
        Assert.DoesNotContain(Months(overview), m => m.GetProperty("upcoming").GetBoolean());
    }

    [Fact]
    public async Task The_review_part_matches_the_overview()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var salary = await SalaryAsync(app, app.AdultA, startsOn: "2023-06-01");
        var property = await RentalAsync(app, app.AdultA);
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/income/sources/{salary}/one-offs",
            new { occurredOn = "2024-06-15", amount = new { amount = "500", currency = "EUR" }, note = "Prime" }));

        var past = await ReviewAsync(app, 2024);
        var current = await ReviewAsync(app, 2025);

        Assert.Equal(Amount((await OverviewAsync(app, "?period=year&year=2024")).GetProperty("figures").GetProperty("total")), $"{past.Total:0.00}");
        Assert.Equal(Amount((await OverviewAsync(app)).GetProperty("figures").GetProperty("elapsed")), $"{current.Total:0.00}");
        Assert.Equal([(salary.ToString(), "Salaire", "salary", 24000m), (property.ToString(), "Studio", "rent", 9600m), ("one_off", null, null, 500m)],
            past.Sources.Select(s => (s.Key, s.Name, s.Kind, s.Amount!.Value)));
        Assert.Equal(new ItemId(salary), past.Sources[0].Id);
        Assert.Null(past.Sources[2].Id);
        Assert.Empty(past.MissingCurrencies);
    }

    [Fact]
    public async Task A_foreign_salary_converts_at_each_date()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SalaryAsync(app, app.AdultA, "Dollars", currency: "USD");
        var days = Enumerable.Range(0, 1000).Select(i => new LocalDate(2023, 1, 1).PlusDays(i)).TakeWhile(d => d <= NidApp.GoldenCaseToday);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IFxRateStore>()
                .WriteAsync(days.Select(d => new FxRate(d, Currency.Eur, d.Year == 2025 ? 0.8m : 0.9m, FxRateSource.Ecb)).ToList(), TestContext.Current.CancellationToken);

        var past = await ReviewAsync(app, 2024);
        var current = await ReviewAsync(app, 2025);

        Assert.Equal((21600m, 16000m), (past.Total, current.Total));
        Assert.Equal(Amount((await OverviewAsync(app, "?period=year&year=2024")).GetProperty("figures").GetProperty("total")), $"{past.Total:0.00}");
        Assert.Equal(Amount((await OverviewAsync(app)).GetProperty("figures").GetProperty("elapsed")), $"{current.Total:0.00}");
    }

    [Fact]
    public async Task A_review_without_a_rate_has_no_total_and_names_the_currency()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SalaryAsync(app, app.AdultA, "Dollars", currency: "USD");

        var current = await ReviewAsync(app, 2025);

        Assert.Null(current.Total);
        Assert.Null(Assert.Single(current.Sources).Amount);
        Assert.Equal(["USD"], current.MissingCurrencies);
    }

    [Fact]
    public async Task A_year_out_of_range_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);

        foreach (var query in new[] { "?period=year&year=2026", "?period=year&year=1900", "?period=12m&year=2024" })
        {
            var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Overview}{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal("2025-01-01", (await OverviewAsync(app, "?year=2025")).GetProperty("from").GetString());
    }
}
