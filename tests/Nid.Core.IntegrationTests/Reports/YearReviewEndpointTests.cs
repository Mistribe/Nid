using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.LoanApi;

namespace Nid.Core.IntegrationTests.Reports;

/// <summary>
/// Issue #12: <c>GET /reports/year</c> agrees with the Performance page and the net worth curve. Today is 2025-10-15: 2024 is the
/// full year, 2025 the year to date.
/// </summary>
public class YearReviewEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Review = "/api/v1/reports/year";
    private const string Performance = "/api/v1/investments/overview/performance";

    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static string? A(JsonElement element, string name)
        => element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : element.GetProperty(name).GetProperty("amount").GetString();

    private static decimal D(string? amount) => decimal.Parse(amount!, CultureInfo.InvariantCulture);

    /// <summary>A PEA opened with 20 000 on 31/12/2023 worth 21 000, 2 000 paid in on 15/06/2024, worth 25 000 on 31/12/2024 and 26 000 on 30/09/2025.</summary>
    private static async Task<ItemId> PeaAsync(NidApp app, string name, PersonId owner, Visibility visibility)
    {
        var id = await app.NewEnvelopeAsync(name, owner, visibility);
        var url = $"/api/v1/investments/envelopes/{id.Value}";
        await app.RawAsync(owner, HttpMethod.Post, $"{url}/flows", new { occurredOn = "2023-12-31", kind = "opening", amount = Eur("20000.00") });
        await app.RawAsync(owner, HttpMethod.Post, $"{url}/flows", new { occurredOn = "2024-06-15", kind = "contribution", amount = Eur("2000.00") });
        await app.RawAsync(owner, HttpMethod.Post, $"{url}/valuations", new { asOf = "2023-12-31", amount = Eur("21000.00") });
        await app.RawAsync(owner, HttpMethod.Post, $"{url}/valuations", new { asOf = "2024-12-31", amount = Eur("25000.00") });
        await app.RawAsync(owner, HttpMethod.Post, $"{url}/valuations", new { asOf = "2025-09-30", amount = Eur("26000.00") });
        return id;
    }

    [Fact]
    public async Task The_investments_section_matches_the_performance_page()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PeaAsync(app, "PEA", app.AdultA, Visibility.Shared);
        var late = await app.NewEnvelopeAsync("Récent", app.AdultB, Visibility.Shared);
        await app.RawAsync(app.AdultB, HttpMethod.Post, $"/api/v1/investments/envelopes/{late.Value}/valuations", new { asOf = "2024-03-01", amount = Eur("100.00") });

        var review = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?year=2024&scope=household"))).GetProperty("investments");
        var page = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Performance}?scope=household&from=2023-12-31&to=2024-12-31"));

        Assert.Equal(("21000.00", "25100.00", "2100.00"), (A(review, "startValue"), A(review, "endValue"), A(review, "gain")));
        Assert.Equal((A(page, "startValue"), A(page, "endValue"), A(page, "contributions"), A(page, "withdrawals"), A(page, "performance")),
            (A(review, "startValue"), A(review, "endValue"), A(review, "contributions"), A(review, "withdrawals"), A(review, "gain")));
        Assert.Equal(page.GetProperty("periodReturnPct").GetDecimal(), review.GetProperty("periodReturnPct").GetDecimal());
        static string? Pct(JsonElement row) => row.GetProperty("periodReturnPct").ValueKind == JsonValueKind.Null ? null : row.GetProperty("periodReturnPct").GetRawText();
        static IEnumerable<(Guid, string?, string?, string?, string?, string?, bool)> Rows(JsonElement body, string gain)
            => body.GetProperty("envelopes").EnumerateArray().Select(r => (r.GetProperty("id").GetGuid(), A(r, "startValue"), A(r, "endValue"),
                A(r, "netContributions"), A(r, gain), Pct(r), r.GetProperty("staleStart").GetBoolean()));
        Assert.Equal(Rows(page, "performanceConverted"), Rows(review, "gain"));
        Assert.All(Rows(review, "gain"), r => Assert.NotNull(r.Item5));
    }

    [Fact]
    public async Task The_net_worth_matches_the_curve()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PeaAsync(app, "PEA", app.AdultA, Visibility.Shared);
        await app.NewPropertyAsync("Maison", Visibility.Shared, (app.AdultA, 10_000));

        var body = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?year=2024&scope=household"));
        var worth = body.GetProperty("netWorth");
        Assert.Empty(body.GetProperty("findings").EnumerateArray());
        var points = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/dashboard/net-worth?scope=household&period=3y")))
            .GetProperty("points").EnumerateArray().ToDictionary(p => p.GetProperty("on").GetString()!, p => A(p, "value"));

        Assert.Equal((points["2023-12-31"], points["2024-12-31"]), (A(worth, "start"), A(worth, "end")));
        Assert.Equal(D(A(worth, "end")) - D(A(worth, "start")), D(A(worth, "change")));
        Assert.Equal(D(A(worth, "change")), worth.GetProperty("parts").EnumerateArray().Sum(p => D(A(p, "amount"))));
    }

    [Fact]
    public async Task An_envelope_closed_in_the_year_agrees()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PeaAsync(app, "PEA", app.AdultA, Visibility.Shared);
        var closed = await app.NewEnvelopeAsync("Clos", app.AdultA, Visibility.Shared);
        var url = $"/api/v1/investments/envelopes/{closed.Value}";
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/flows", new { occurredOn = "2023-12-31", kind = "opening", amount = Eur("10000.00") });
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/valuations", new { asOf = "2023-12-31", amount = Eur("10500.00") });
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/close",
            new { closedOn = "2024-10-01", finalWithdrawal = Eur("11000.00") })).StatusCode);

        var body = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?year=2024&scope=household"));

        var investments = body.GetProperty("investments");
        var parts = body.GetProperty("netWorth").GetProperty("parts").EnumerateArray()
            .ToDictionary(p => p.GetProperty("key").GetString()!, p => D(A(p, "amount")));
        Assert.Equal(D(A(investments, "endValue")) - D(A(investments, "startValue")), parts["investments.paid_in"] + parts["investments.value"]);
        // From its closing, the closed envelope is worth 0 and keeps its net paid in: the split is the section's flows and gain.
        Assert.Equal((2_000m - 11_000m, 2_500m), (parts["investments.paid_in"], parts["investments.value"]));
        Assert.Equal((D(A(investments, "contributions")) - D(A(investments, "withdrawals")), D(A(investments, "gain"))),
            (parts["investments.paid_in"], parts["investments.value"]));
        Assert.Equal(2, investments.GetProperty("envelopes").GetArrayLength());
    }

    [Theory]
    [InlineData("household")]
    [InlineData("me")]
    public async Task Another_adults_private_items_are_left_out(string scope)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await PeaAsync(app, "PEA", app.AdultA, Visibility.Shared);
        var hidden = await PeaAsync(app, "Secret de B", app.AdultB, Visibility.Private);
        // B's private property (bought in 2020) and loan (from 2025): in neither year's net worth.
        var house = await app.NewPropertyAsync("Maison de B", Visibility.Private, (app.AdultB, 10_000));
        var loan = await app.NewLoanAsync("Prêt de B", Visibility.Private, (app.AdultB, 10_000));

        foreach (var (year, end) in new[] { ("2024", "25000.00"), ("2025", "26000.00") })
        {
            var body = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?year={year}&scope={scope}"));

            Assert.Equal([mine.Value], body.GetProperty("investments").GetProperty("envelopes").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
            Assert.Equal(end, A(body.GetProperty("netWorth"), "end"));
            Assert.DoesNotContain(body.GetProperty("netWorth").GetProperty("parts").EnumerateArray(),
                p => p.GetProperty("key").GetString()!.StartsWith("real_estate.", StringComparison.Ordinal));
            foreach (var secret in new[] { hidden.Value.ToString(), house.Value.ToString(), loan.Value.ToString(), "Secret de B", "Maison de B", "Prêt de B" })
                Assert.DoesNotContain(secret, body.ToString());
        }
    }

    [Fact]
    public async Task The_current_year_runs_to_today()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PeaAsync(app, "PEA", app.AdultA, Visibility.Shared);

        var body = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?scope=household"));
        // The previous period ends on the same day a year earlier.
        var previous = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Performance}?scope=household&from=2023-12-31&to=2024-10-15"));

        Assert.Equal((2025, "2024-12-31", "2025-10-15", true),
            (body.GetProperty("year").GetInt32(), body.GetProperty("start").GetString(), body.GetProperty("end").GetString(), body.GetProperty("toDate").GetBoolean()));
        Assert.Equal([2023, 2024, 2025], body.GetProperty("years").EnumerateArray().Select(y => y.GetInt32()));
        Assert.Equal(A(previous, "performance"), A(body.GetProperty("investments"), "previousGain"));
        Assert.Equal(("25000.00", "26000.00"), (A(body.GetProperty("netWorth"), "start"), A(body.GetProperty("netWorth"), "end")));
    }

    [Fact]
    public async Task A_missing_rate_says_why_the_net_worth_is_absent()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var dollars = await app.NewEnvelopeAsync("Brokerage", app.AdultA, Visibility.Shared, "USD");
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{dollars.Value}/valuations",
            new { asOf = "2023-12-31", amount = new { amount = "1000.00", currency = "USD" } });

        var body = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?year=2024&scope=household"));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("netWorth").ValueKind);
        Assert.Equal(["core.fx_rate_missing"], body.GetProperty("findings").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public async Task A_future_year_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PeaAsync(app, "PEA", app.AdultA, Visibility.Shared);

        var future = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?year=2026");
        var before = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?year=2022");
        var unreadable = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?year=soon");

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (future.StatusCode, before.StatusCode, unreadable.StatusCode));
        Assert.Contains("reports.invalid_year", await future.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("reports.invalid_year", await before.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A monthly salary of <paramref name="amount"/> from <paramref name="startsOn"/>, until <paramref name="endsOn"/> when given.</summary>
    private static async Task<Guid> SalaryAsync(
        NidApp app, PersonId owner, string name = "Salaire", string visibility = "shared", string? endsOn = null, string amount = "2000",
        string startsOn = "2024-01-01")
        => await CreatedAsync(await app.RawAsync(owner, HttpMethod.Post, "/api/v1/income/sources", new
        {
            displayName = name, kind = "salary", currency = "EUR", visibility,
            owners = new[] { new { personId = owner.Value, shareBp = 10_000 } },
            phases = new[]
            {
                new { label = "Salaire", startsOn, endsOn, netAmount = new { amount, currency = "EUR" }, every = new { count = 1, unit = "month" } },
            },
        }));

    /// <summary>A subscription of <paramref name="price"/> charged on the 1st of each month from 2024-01-01.</summary>
    private static async Task<ItemId> SubscriptionAsync(NidApp app, PersonId owner, string name, Visibility visibility, string price = "30")
    {
        var id = await app.NewSubscriptionAsync(name, owner, visibility);
        (await app.RawAsync(owner, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2024-01-01", price = new { amount = price, currency = "EUR" }, every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        return id;
    }

    /// <summary>Archives an item on a day, mid-morning UTC: the same day in the household's zone.</summary>
    private static async Task ArchiveAsync(NidApp app, ItemId id, int year, int month, int day)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        Assert.True((await scope.ServiceProvider.GetRequiredService<IItemRegistry>()
            .ArchiveAsync(id, Instant.FromUtc(year, month, day, 10, 0), TestContext.Current.CancellationToken)).IsSuccess);
    }

    private static async Task<JsonElement> IncomeAsync(NidApp app, string year)
        => (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?year={year}&scope=household"))).GetProperty("income");

    private static Dictionary<string, string?> Engagements(JsonElement income)
        => income.GetProperty("engagementSources").EnumerateArray().ToDictionary(s => s.GetProperty("source").GetString()!, s => A(s, "amount"));

    [Fact]
    public async Task The_income_matches_the_overview()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var salary = await SalaryAsync(app, app.AdultA, amount: "5000");
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/income/sources/{salary}/one-offs",
            new { occurredOn = "2024-06-15", amount = Eur("500"), note = "Prime" }));

        var past = await IncomeAsync(app, "2024");
        var current = await IncomeAsync(app, "2025");

        static async Task<string?> OverviewAsync(NidApp app, string query, string figure)
            => A((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/income/overview?scope=household{query}"))).GetProperty("figures"), figure);
        Assert.Equal("60500.00", A(past, "income"));
        Assert.Equal(await OverviewAsync(app, "&period=year&year=2024", "total"), A(past, "income"));
        Assert.Equal(await OverviewAsync(app, "&period=year", "elapsed"), A(current, "income"));
        Assert.Equal([(salary.ToString(), "60000.00"), ("one_off", "500.00")],
            past.GetProperty("sources").EnumerateArray().Select(s => (s.GetProperty("key").GetString(), A(s, "amount"))));
        Assert.Equal(salary, past.GetProperty("sources")[0].GetProperty("id").GetGuid());
        // 2025: ten months of the year's tax estimate; none before the rules.
        var tax = D((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/tax?scope=household&year=2025")))
            .GetProperty("result").GetProperty("totalTax").GetString());
        Assert.True(tax > 0m);
        Assert.Equal(Math.Round(tax * 10 / 12, 2, MidpointRounding.AwayFromZero), D(Engagements(current)[CommitmentSources.Tax]));
        Assert.True(current.GetProperty("engagementSources").EnumerateArray()
            .Single(s => s.GetProperty("source").GetString() == CommitmentSources.Tax).GetProperty("estimated").GetBoolean());
        Assert.DoesNotContain(CommitmentSources.Tax, Engagements(past).Keys);
        Assert.Equal(D(A(current, "income")) - D(A(current, "engagements")), D(A(current, "margin")));
    }

    [Fact]
    public async Task The_engagements_are_the_months_cash()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SalaryAsync(app, app.AdultA); // income since 2024: the year is on offer
        await SubscriptionAsync(app, app.AdultA, "Presse", Visibility.Shared);
        await NewAsync(app, Golden(app));

        var past = await IncomeAsync(app, "2024");

        var cash = new Dictionary<string, decimal>();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
            foreach (var month in Enumerable.Range(1, 12))
                foreach (var contribution in scope.ServiceProvider.GetServices<ICommitmentsContribution>())
                {
                    var result = await contribution.CommitmentsAsync(app.AdultA, VisibilityScope.Household,
                        new LocalDate(2024, month, 1).PlusMonths(1).PlusDays(-1), TestContext.Current.CancellationToken, includeArchived: true);
                    Assert.True(result.TryGetValue(out var part));
                    cash[part!.Source] = cash.GetValueOrDefault(part.Source) + part.Cash.GetValueOrDefault(Currency.Eur);
                }
        }
        var engagements = Engagements(past);
        Assert.Equal((360m, 360m), (cash[CommitmentSources.Subscriptions], D(engagements[CommitmentSources.Subscriptions])));
        Assert.True(cash[CommitmentSources.Loans] > 0m);
        Assert.Equal(cash[CommitmentSources.Loans], D(engagements[CommitmentSources.Loans]));
        Assert.Equal(engagements.Values.Sum(D), D(A(past, "engagements")));
        Assert.Equal(24_000m - D(A(past, "engagements")), D(A(past, "margin")));
    }

    [Fact]
    public async Task An_archived_subscription_counts_in_its_year()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SalaryAsync(app, app.AdultA);
        var presse = await SubscriptionAsync(app, app.AdultA, "Presse", Visibility.Shared);
        await SubscriptionAsync(app, app.AdultA, "Musique", Visibility.Shared, "10");
        await ArchiveAsync(app, presse, 2024, 6, 15);

        // Presse: January to June 2024. Musique: the twelve months, then ten in 2025 (October's is due on the 1st).
        Assert.Equal("300.00", Engagements(await IncomeAsync(app, "2024"))[CommitmentSources.Subscriptions]);
        Assert.Equal("100.00", Engagements(await IncomeAsync(app, "2025"))[CommitmentSources.Subscriptions]);
    }

    [Theory]
    [InlineData("household")]
    [InlineData("me")]
    public async Task Another_adults_private_income_and_subscription_are_left_out(string scope)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var salary = await SalaryAsync(app, app.AdultA);
        await SubscriptionAsync(app, app.AdultA, "Presse", Visibility.Shared);
        var hidden = await SalaryAsync(app, app.AdultB, "Salaire secret de B", "private", amount: "5000");
        var secret = await SubscriptionAsync(app, app.AdultB, "Abonnement secret de B", Visibility.Private, "99");
        var loan = await NewAsync(app, Golden(app, "Prêt secret de B", "private", app.AdultB), app.AdultB);
        var care = await ChildcareAsync(app, app.AdultB, "Garde secrète de B", "private");

        foreach (var (year, income, subscriptions) in new[] { ("2024", "24000.00", "360.00"), ("2025", "20000.00", "300.00") })
        {
            var body = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?year={year}&scope={scope}"));
            var section = body.GetProperty("income");

            Assert.Equal([salary], section.GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("id").GetGuid()));
            Assert.Equal((income, subscriptions), (A(section, "income"), Engagements(section)[CommitmentSources.Subscriptions]));
            Assert.Equal([CommitmentSources.Subscriptions], Engagements(section).Keys.Where(k => k != CommitmentSources.Tax));
            foreach (var leak in new[] { hidden.ToString(), secret.Value.ToString(), loan.ToString(), care.ToString(),
                         "Salaire secret de B", "Abonnement secret de B", "Prêt secret de B", "Garde secrète de B" })
                Assert.DoesNotContain(leak, body.ToString());
        }
    }

    /// <summary>A childcare arrangement of <paramref name="owner"/> for the dependent from 2024-01-01, Monday to Friday 08:00–17:00.</summary>
    private static async Task<Guid> ChildcareAsync(NidApp app, PersonId owner, string name, string visibility)
        => await CreatedAsync(await app.RawAsync(owner, HttpMethod.Post, "/api/v1/childcare", new
        {
            name, childId = app.Dependent.Value,
            carer = new { name = "Nounou", pajemploi = "P1", approval = (string?)null },
            terms = new
            {
                from = "2024-01-01", netHourlyRate = "5.00", grossHourlyRate = (string?)null, overtimePct = "10", upkeepPer9h = "4.00", lunchPrice = "3.00",
                weekTypes = new[] { new { name = "Semaine", weeks = 46, schedule = Enumerable.Range(1, 5).Select(d => new { day = d, arrival = "08:00", departure = "17:00", lunch = true }).ToArray() } },
            },
            gaps = Array.Empty<object>(), visibility,
            owners = new[] { new { personId = owner.Value, shareBp = 10_000 } },
        }));

    [Fact]
    public async Task A_loan_not_yet_started_is_no_engagement()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SalaryAsync(app, app.AdultA, startsOn: "2023-01-01");
        await NewAsync(app, Short(app)); // from 2025-01-15

        var past = await IncomeAsync(app, "2024");

        Assert.DoesNotContain(CommitmentSources.Loans, Engagements(past).Keys);
        Assert.DoesNotContain(CommitmentSources.Loans, Engagements(await IncomeAsync(app, "2023")).Keys);
        Assert.Equal(("0.00", "24000.00"), (A(past, "engagements"), A(past, "previousMargin")));
        Assert.Equal("10000.00", Engagements(await IncomeAsync(app, "2025"))[CommitmentSources.Loans]);
    }

    [Fact]
    public async Task A_foreign_source_without_rates_opens_its_years()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/income/sources", new
        {
            displayName = "Salary", kind = "salary", currency = "USD", visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            phases = new[] { new { label = "Salary", startsOn = "2023-06-01", endsOn = (string?)null, netAmount = new { amount = "2000", currency = "USD" }, every = new { count = 1, unit = "month" } } },
        }));

        var body = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?year=2025&scope=household"));

        Assert.Equal([2023, 2024, 2025], body.GetProperty("years").EnumerateArray().Select(y => y.GetInt32()));
    }

    [Fact]
    public async Task The_previous_year_is_beside()
    {
        await using var app = await NidApp.StartAsync(fixture);
        // All of 2024 before 15 October: the previous period of 2025 to date holds the same figures as the year 2024.
        var salary = await SalaryAsync(app, app.AdultA, endsOn: "2024-06-30");
        var presse = await SubscriptionAsync(app, app.AdultA, "Presse", Visibility.Shared);
        await ArchiveAsync(app, presse, 2024, 6, 15);
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/income/sources/{salary}/one-offs",
            new { occurredOn = "2025-03-01", amount = Eur("500"), note = "Prime" }));

        var past = await IncomeAsync(app, "2024");
        var current = await IncomeAsync(app, "2025");

        Assert.Equal(("12000.00", "180.00", "11820.00"), (A(past, "income"), A(past, "engagements"), A(past, "margin")));
        Assert.Equal((A(past, "income"), A(past, "engagements"), A(past, "margin")),
            (A(current, "previousIncome"), A(current, "previousEngagements"), A(current, "previousMargin")));
        Assert.Equal("500.00", A(current, "income"));
    }

    [Fact]
    public async Task Income_without_a_holding_opens_its_years()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SalaryAsync(app, app.AdultA, startsOn: "2023-06-01");

        var body = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?year=2024&scope=household"));

        Assert.Equal([2023, 2024, 2025], body.GetProperty("years").EnumerateArray().Select(y => y.GetInt32()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("netWorth").ValueKind);
        Assert.Equal("24000.00", A(body.GetProperty("income"), "income"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // the net worth misses the same rate
    public async Task A_subscription_without_a_rate_is_one_finding(bool foreignHolding)
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SalaryAsync(app, app.AdultA);
        var id = await app.NewSubscriptionAsync("Streaming", app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2024-01-01", price = new { amount = "10", currency = "USD" }, every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        if (foreignHolding)
        {
            var dollars = await app.NewEnvelopeAsync("Brokerage", app.AdultA, Visibility.Shared, "USD");
            await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{dollars.Value}/valuations",
                new { asOf = "2023-12-31", amount = new { amount = "1000.00", currency = "USD" } });
        }

        var body = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Review}?year=2025&scope=household"));
        var income = body.GetProperty("income");

        Assert.Equal(["core.fx_rate_missing"], body.GetProperty("findings").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal((null, null, null), (Engagements(income)[CommitmentSources.Subscriptions], A(income, "engagements"), A(income, "margin")));
        Assert.Equal("20000.00", A(income, "income"));
    }
}
