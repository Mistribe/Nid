using System.Globalization;
using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Core.Domain.Projection;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.ProjectionApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.Core.IntegrationTests.Projection;

/// <summary>
/// Net worth projection spec §4, §6 and §7: <c>POST /api/v1/scenarios/projection/simulate</c>, a projection scenario's document
/// against the base, nothing written. Today is 2025-10-15: point k is the last day of October 2025 + k months.
/// </summary>
public class ProjectionScenarioSimulateTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Url = "/api/v1/scenarios/projection/simulate";

    private static object Document(string horizon = "10y", object? assumptions = null, params object[] events)
        => new { version = 1, horizon, assumptions = assumptions ?? new { }, events };

    private static async Task<JsonElement> SimulateAsync(NidApp app, object document, string scope = "household", PersonId? reader = null)
    {
        var response = await app.RawAsync(reader ?? app.AdultA, HttpMethod.Post, Url, new { scope, document });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<ItemId> ValuedEnvelopeAsync(NidApp app, string amount, string name = "Livret")
    {
        var envelope = await app.NewEnvelopeAsync(name, app.AdultA, Visibility.Shared);
        Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{envelope.Value}/valuations",
            new { asOf = "2025-09-30", amount = new { amount, currency = "EUR" }, note = (string?)null })).StatusCode);
        return envelope;
    }

    /// <summary>A purchase scenario saved through its route: 215 000 on 2026-01-10, 30 000 from <paramref name="funding"/>, 200 000 borrowed.</summary>
    private static async Task<Guid> PurchaseAsync(NidApp app, ItemId? funding = null)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/scenarios", new
        {
            name = "Appartement", visibility = "shared", scope = "household", owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            document = new
            {
                version = 1, use = "primary", purchaseOn = "2026-01-10", price = "215000", notaryFees = "15000", currentRent = "800", monthlyCharges = "100",
                funding = funding is { } f ? new object[] { new { item = f.Value, amount = "30000" } } : [],
                loans = new[]
                {
                    new
                    {
                        name = "Banque", amount = "200000", rate = "3.5", months = 240, insurance = new { kind = "rate_on_initial", value = "0.3" },
                        fileFees = "1000", guaranteeFees = "0",
                    },
                },
                assumptions = new
                {
                    horizonYears = 10, classRates = new Dictionary<string, string> { ["cash"] = "2", ["equity"] = "6" }, monthlySavings = "500",
                    savingsClass = "cash",
                },
            },
        });
        return await CreatedAsync(response);
    }

    private static List<JsonElement> List(JsonElement view, string name) => view.GetProperty(name).EnumerateArray().ToList();

    private static string? Str(JsonElement element, string name) => element.GetProperty(name).GetString();

    private static decimal Dec(JsonElement element, string name) => decimal.Parse(Str(element, name)!, CultureInfo.InvariantCulture);

    private static List<JsonElement> Points(JsonElement comparison, string series) => List(comparison.GetProperty(series), "points");

    private static List<string> Raw(IEnumerable<JsonElement> points) => points.Select(p => p.GetRawText()).ToList();

    private static List<(string? Code, string? Detail)> Warnings(JsonElement view)
        => List(view, "warnings").Select(w => (Str(w, "code"), Str(w, "detail"))).ToList();

    /// <summary>The savings class's monthly rate as the engine computes it (cash, 2 % a year).</summary>
    private static decimal CashMonthly => (decimal)Math.Pow(1.02, 1.0 / 12) - 1m;

    [Fact]
    public async Task Income_drop_at_retirement_lowers_the_curve()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ValuedEnvelopeAsync(app, "10000.00");

        var view = await SimulateAsync(app, Document(events: new
        {
            id = Guid.NewGuid(), kind = "income_change", label = "Retraite", start = new { on = "2030-01-01" }, monthlyAmount = "-1800",
        }));

        // January 2030 is point 51: the drop, in euros of January 2030 (inflation spec C17), is added to the 70 monthly savings from
        // there to point 120 as −1800 × g(k) ÷ g(51), each compounding after.
        var g = ProjectionRates.Resolve(ProjectionDefaults.Assumptions, null, new LocalDate(2025, 10, 15), 120, []).Income;
        var compounded = 0m;
        for (var k = 51; k <= 120; k++) compounded = compounded * (1m + CashMonthly) - 1800m * g[k] / g[51];
        var (based, scenario) = (Points(view, "base"), Points(view, "scenario"));
        Assert.Equal(121, scenario.Count);
        Assert.Equal(Str(based[50], "savings"), Str(scenario[50], "savings"));
        Assert.InRange(Dec(scenario[^1], "savings") - Dec(based[^1], "savings"), compounded - 0.01m, compounded + 0.01m);
        Assert.Equal(Dec(scenario[^1], "nominal") - Dec(based[^1], "nominal"), Dec(view, "gapNominal"));
        Assert.True(Dec(view, "gapNominal") < 0m);
        Assert.True(Dec(view, "gapReal") < 0m);
        // The gap at each point, on the scenario's grid; the last one is the horizon's.
        var gaps = List(view, "gaps");
        Assert.Equal(scenario.Count, gaps.Count);
        Assert.Equal((Str(view, "gapNominal"), Str(view, "gapReal")), (Str(gaps[^1], "nominal"), Str(gaps[^1], "real")));
        Assert.Equal(Str(scenario[80], "on"), Str(gaps[80], "on"));
        Assert.InRange(Dec(gaps[80], "nominal"), Dec(scenario[80], "nominal") - Dec(based[80], "nominal") - 0.01m,
            Dec(scenario[80], "nominal") - Dec(based[80], "nominal") + 0.01m);
        Assert.InRange(Dec(gaps[80], "real"), Dec(scenario[80], "real") - Dec(based[80], "real") - 0.01m,
            Dec(scenario[80], "real") - Dec(based[80], "real") + 0.01m);
        Assert.Equal("0.00", Str(gaps[10], "nominal"));
        var milestone = Assert.Single(List(view.GetProperty("scenario"), "milestones"), m => Str(m, "kind") == "event");
        Assert.Equal(("2030-01-01", "Retraite"), (Str(milestone, "on"), Str(milestone, "label")));
        var echoed = Assert.Single(List(view.GetProperty("document"), "events"));
        Assert.Equal(("income_change", "-1800.00", "2030-01-01"), (Str(echoed, "kind"), Str(echoed, "monthlyAmount"), Str(echoed.GetProperty("start"), "on")));
    }

    [Fact]
    public async Task Stopping_savings_matches_a_zero_savings_base()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ValuedEnvelopeAsync(app, "10000.00");
        // Inflation spec C17: S₀ follows inflation, S₀ × i(k), and a savings change from today is indexed from today, −500 × i(k):
        // the pause cancels S₀ exactly at any inflation.
        await HouseholdSavingsAsync(app, "500");

        var view = await SimulateAsync(app, Document(events: new
        {
            id = Guid.NewGuid(), kind = "savings_change", label = "Pause", start = new { on = "2025-10-15" }, monthlyAmount = "-500",
        }));

        await HouseholdSavingsAsync(app, "0");
        var zero = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/projection")).RootElement;
        Assert.Equal(Raw(List(zero, "points")), Raw(Points(view, "scenario")));
        Assert.NotEqual(Raw(List(zero, "points")), Raw(Points(view, "base")));
    }

    [Fact]
    public async Task Observed_inflation_indexes_savings_incomes_and_events() // inflation spec D5, C17
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.SeedCpiAsync(new YearMonth(2025, 9), 3m);
        await HouseholdSavingsAsync(app, "500");
        await IncomeSourceAsync(app, "Salaire", "salary", "2020-01-01", "3000");

        var view = await SimulateAsync(app, Document(events: new
        {
            id = Guid.NewGuid(), kind = "savings_change", label = "Moins", start = new { on = "2026-03-01" }, monthlyAmount = "-200",
        }));

        // S(k) = 500 × i(k) + 3000 × g(k) − 3000 × i(k) − 200 × i(k) ÷ i(5) from March 2026 (point 5), i(k) at the observed 3 % for
        // 2025 and the long-term 2 % after; the pot grows at the cash rate.
        static List<decimal> Expected(ResolvedRates rates, bool change)
        {
            var (i, g, cash) = (rates.Inflation, rates.Income, rates.MonthlyClassRate[AllocationClasses.Cash]);
            var pot = 0m;
            var points = new List<decimal> { 0m };
            for (var k = 1; k <= 120; k++)
            {
                pot = pot * (1m + cash[k]) + 500m * i[k] + 3000m * g[k] - 3000m * i[k] + (change && k >= 5 ? -200m * i[k] / i[5] : 0m);
                points.Add(pot);
            }
            return points;
        }
        var today = new LocalDate(2025, 10, 15);
        var observed = ProjectionRates.Resolve(ProjectionDefaults.Assumptions, 0.03m, today, 120, [AllocationClasses.Cash]);
        var longTerm = ProjectionRates.Resolve(ProjectionDefaults.Assumptions, null, today, 120, [AllocationClasses.Cash]);
        var (based, scenario) = (Points(view, "base"), Points(view, "scenario"));
        Assert.Equal(("3.0", "3.0"), (Str(view.GetProperty("base"), "observedInflation"), Str(view.GetProperty("scenario"), "observedInflation")));
        foreach (var k in new[] { 1, 2, 3, 4, 5, 6, 12, 120 })
        {
            Assert.InRange(Dec(based[k], "savings"), Expected(observed, false)[k] - 0.01m, Expected(observed, false)[k] + 0.01m);
            Assert.InRange(Dec(scenario[k], "savings"), Expected(observed, true)[k] - 0.01m, Expected(observed, true)[k] + 0.01m);
            Assert.InRange(Dec(scenario[k], "real"), Dec(scenario[k], "nominal") / observed.Inflation[k] - 0.01m,
                Dec(scenario[k], "nominal") / observed.Inflation[k] + 0.01m);
        }
        // Positive control: at the long-term rate alone the figures differ by more than a cent.
        Assert.True(Math.Abs(Dec(scenario[6], "savings") - Expected(longTerm, true)[6]) > 0.05m);
    }

    [Fact]
    public async Task Early_repayment_moves_the_loan_end()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ValuedEnvelopeAsync(app, "80000.00");
        var loan = await NewAsync(app, Golden(app));

        var view = await SimulateAsync(app, Document("20y", null, new
        {
            id = Guid.NewGuid(), kind = "early_repayment", label = "Prime", start = new { on = "2026-01-20" }, amount = "50000",
            split = "chosen", loan, effect = "reduce_term", penalty = "500",
        }));

        string End(string series) => Str(Assert.Single(List(view.GetProperty(series), "milestones"),
            m => Str(m, "kind") == "loan_end" && m.GetProperty("item").GetGuid() == loan), "on")!;
        Assert.Equal("2040-10-15", End("base"));
        Assert.True(string.CompareOrdinal(End("scenario"), "2040-10-15") < 0, End("scenario"));
        var milestone = Assert.Single(List(view.GetProperty("scenario"), "milestones"), m => Str(m, "kind") == "event");
        Assert.Equal(("2026-01-20", "Prime", loan), (Str(milestone, "on"), Str(milestone, "label"), milestone.GetProperty("item").GetGuid()));
        // January 2026 is point 3: the debt drops by the amount, the savings by the amount and the penalty.
        var (based, scenario) = (Points(view, "base"), Points(view, "scenario"));
        Assert.Equal(Str(based[2], "nominal"), Str(scenario[2], "nominal"));
        Assert.InRange(Dec(based[3], "debt") - Dec(scenario[3], "debt"), 50_000m - 0.01m, 50_000m + 0.01m);
        Assert.InRange(Dec(based[3], "savings") - Dec(scenario[3], "savings"), 50_500m - 0.01m, 50_500m + 0.01m);
    }

    [Fact]
    public async Task Sale_adds_proceeds_and_removes_the_property()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{home}/valuations", new { asOf = "2025-06-01", amount = "300000" }));
        var loan = Golden(app);
        loan["propertyId"] = home;
        await NewAsync(app, loan);

        // Freed payments kept out of the savings, so the only change of the savings at the sale is its proceeds.
        var view = await SimulateAsync(app, Document(assumptions: new { freedPaymentsToSavings = false }, events: new
        {
            id = Guid.NewGuid(), kind = "property_sale", start = new { on = "2026-03-01" }, property = home, price = "320000", costs = "10000",
        }));

        // March 2026 is point 5; the loan is settled by its balance after the 15 February payment, the debt at point 4.
        var (based, scenario) = (Points(view, "base"), Points(view, "scenario"));
        Assert.Equal(("0.00", "0.00"), (Str(scenario[5], "property"), Str(scenario[5], "debt")));
        Assert.True(Dec(based[5], "property") > 300_000m);
        var proceeds = 320_000m - 10_000m - Dec(based[4], "debt");
        Assert.InRange(Dec(scenario[5], "savings") - Dec(based[5], "savings"), proceeds - 0.01m, proceeds + 0.01m);
        Assert.Equal(("0.00", "0.00"), (Str(scenario[^1], "property"), Str(scenario[^1], "debt")));
        var milestone = Assert.Single(List(view.GetProperty("scenario"), "milestones"), m => Str(m, "kind") == "event");
        Assert.Equal(("2026-03-01", "property_sale", home), (Str(milestone, "on"), Str(milestone, "label"), milestone.GetProperty("item").GetGuid()));
    }

    /// <summary>The golden loan paid on the 20th: today (the 15th) falls before this month's payment.</summary>
    private static Dictionary<string, object?> PaidOnThe20th(NidApp app)
    {
        var loan = Golden(app);
        loan["disbursedOn"] = "2020-10-20";
        loan["firstPaymentDate"] = "2020-11-20";
        return loan;
    }

    /// <summary>The scenario's savings with freed payments on, less with them off, at points 1 and 2.</summary>
    private static async Task<(decimal First, decimal Second)> FreedAsync(NidApp app, object @event)
    {
        var on = Points(await SimulateAsync(app, Document(assumptions: new { freedPaymentsToSavings = true }, events: @event)), "scenario");
        var off = Points(await SimulateAsync(app, Document(assumptions: new { freedPaymentsToSavings = false }, events: @event)), "scenario");
        return (Dec(on[1], "savings") - Dec(off[1], "savings"), Dec(on[2], "savings") - Dec(off[2], "savings"));
    }

    [Fact]
    public async Task Sale_today_frees_the_payments_from_next_month()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{home}/valuations", new { asOf = "2025-06-01", amount = "300000" }));
        var loan = PaidOnThe20th(app);
        loan["propertyId"] = home;
        await NewAsync(app, loan);

        var (first, second) = await FreedAsync(app, new
        {
            id = Guid.NewGuid(), kind = "property_sale", start = new { on = "2025-10-15" }, property = home, price = "320000", costs = "0",
        });

        // The golden payment, 1 159.92, saved each month from point 1, compounding after.
        Assert.InRange(first, 1159.92m - 0.01m, 1159.92m + 0.01m);
        var expected = 1159.92m * (1m + CashMonthly) + 1159.92m;
        Assert.InRange(second, expected - 0.01m, expected + 0.01m);
    }

    [Fact]
    public async Task Repayment_today_reducing_the_payment_frees_the_difference()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await NewAsync(app, PaidOnThe20th(app));

        var (first, _) = await FreedAsync(app, new
        {
            id = Guid.NewGuid(), kind = "early_repayment", start = new { on = "2025-10-15" }, amount = "50000", split = "chosen", loan,
            effect = "reduce_payment", penalty = "0",
        });

        Assert.True(first > 100m, $"{first}");
    }

    [Fact]
    public async Task Purchase_reference_withdraws_the_down_payment_and_adds_the_home()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var livret = await ValuedEnvelopeAsync(app, "100000.00");
        var purchase = await PurchaseAsync(app, livret);

        var view = await SimulateAsync(app, Document(events: new
        {
            id = Guid.NewGuid(), kind = "property_purchase", start = new { on = "2026-01-10" }, scenario = purchase,
        }));

        // January 2026 is point 3: the home enters at its price, its loan at its amount (first payment 2026-02-10).
        var (based, scenario) = (Points(view, "base"), Points(view, "scenario"));
        Assert.Equal(Raw(based.Take(3)), Raw(scenario.Take(3)));
        Assert.Equal(("215000.00", "200000.00"), (Str(scenario[3], "property"), Str(scenario[3], "debt")));
        Assert.Equal(("0.00", "0.00"), (Str(based[3], "property"), Str(based[3], "debt")));
        // The down payment leaves the envelope before its month's growth; the month's cost is the rent saved less the charges.
        var expected = Dec(based[3], "savings") - 30_000m * (1m + CashMonthly) + 700m;
        Assert.InRange(Dec(scenario[3], "savings"), expected - 0.01m, expected + 0.01m);
        // The home grows at the real estate rate (2 %); the loan's balance falls.
        var grown = 215_000m * (decimal)Math.Pow(1.02, 1.0 / 12);
        Assert.InRange(Dec(scenario[4], "property"), grown - 0.01m, grown + 0.01m);
        Assert.True(Dec(scenario[4], "debt") < 200_000m);
        var milestone = Assert.Single(List(view.GetProperty("scenario"), "milestones"), m => Str(m, "kind") == "event");
        Assert.Equal(("2026-01-10", purchase), (Str(milestone, "on"), milestone.GetProperty("item").GetGuid()));
        Assert.DoesNotContain(Warnings(view.GetProperty("scenario")), w => w.Code == "hidden_targets");
    }

    [Fact]
    public async Task Purchase_start_is_not_read()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var purchase = await PurchaseAsync(app);

        var view = await SimulateAsync(app, Document(events: new
        {
            id = Guid.NewGuid(), kind = "property_purchase", start = new { person = Guid.NewGuid(), age = 64 }, scenario = purchase,
        }));

        Assert.DoesNotContain(Warnings(view.GetProperty("scenario")), w => w.Code is "hidden_targets" or "event_undated");
        Assert.Equal("215000.00", Str(Points(view, "scenario")[3], "property"));
    }

    [Fact]
    public async Task At_age_event_without_birth_date_is_dropped_and_warned()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ValuedEnvelopeAsync(app, "10000.00");

        var view = await SimulateAsync(app, Document(events: new
        {
            id = Guid.NewGuid(), kind = "income_change", start = new { person = app.AdultA.Value, age = 64 }, monthlyAmount = "-1800",
        }));

        Assert.Equal(Raw(Points(view, "base")), Raw(Points(view, "scenario")));
        Assert.Equal([("event_undated", "1")], Warnings(view.GetProperty("scenario")));
        Assert.Empty(Warnings(view.GetProperty("base")));
        var echoed = Assert.Single(List(view.GetProperty("document"), "events"));
        Assert.Equal((app.AdultA.Value, 64), (echoed.GetProperty("start").GetProperty("person").GetGuid(), echoed.GetProperty("start").GetProperty("age").GetInt32()));
    }

    [Fact]
    public async Task Deleted_purchase_scenario_counts_as_hidden()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ValuedEnvelopeAsync(app, "10000.00");
        var purchase = await PurchaseAsync(app);
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"/api/v1/scenarios/{purchase}")).StatusCode);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Url, new
        {
            scope = "household",
            document = Document(events: new { id = Guid.NewGuid(), kind = "property_purchase", start = new { on = "2026-01-10" }, scenario = purchase }),
        });
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = JsonDocument.Parse(body).RootElement;

        Assert.Equal([("hidden_targets", "1")], Warnings(view.GetProperty("scenario")));
        Assert.Empty(List(view.GetProperty("document"), "events"));
        Assert.Equal(Raw(Points(view, "base")), Raw(Points(view, "scenario")));
        Assert.DoesNotContain(purchase.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hidden_loan_of_the_other_adult_leaks_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = Short(app, "Prêt secret");
        hidden["visibility"] = "private";
        hidden["borrowers"] = new[] { new { personId = app.AdultB.Value, shareBp = 10_000 } };
        var secret = await NewAsync(app, hidden, app.AdultB);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Url, new
        {
            scope = "me",
            document = Document(events: new
            {
                id = Guid.NewGuid(), kind = "early_repayment", start = new { on = "2025-11-20" }, amount = "4321", split = "chosen", loan = secret,
                effect = "reduce_term", penalty = "0",
            }),
        });
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(secret.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4321", body);
        Assert.DoesNotContain("Prêt secret", body);
        var view = JsonDocument.Parse(body).RootElement;
        Assert.Equal([("hidden_targets", "1")], Warnings(view.GetProperty("scenario")));
    }

    [Fact]
    public async Task Past_event_is_taken_today()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ValuedEnvelopeAsync(app, "10000.00");
        object Change(string on) => new { id = Guid.NewGuid(), kind = "savings_change", start = new { on }, monthlyAmount = "100" };

        var past = await SimulateAsync(app, Document(events: Change("2025-01-01")));
        var today = await SimulateAsync(app, Document(events: Change("2025-10-15")));

        Assert.Equal([("event_in_past", "1")], Warnings(past.GetProperty("scenario")));
        Assert.Empty(Warnings(today.GetProperty("scenario")));
        Assert.Equal(Raw(Points(today, "scenario")), Raw(Points(past, "scenario")));
        Assert.NotEqual(Raw(Points(past, "base")), Raw(Points(past, "scenario")));
        Assert.Equal("2025-01-01", Str(Assert.Single(List(past.GetProperty("document"), "events")).GetProperty("start"), "on"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    public async Task Invalid_document_names_its_field(string amount)
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Url, new
        {
            scope = "household",
            document = Document(events: new
            {
                id = Guid.NewGuid(), kind = "early_repayment", start = new { on = "2026-01-20" }, amount, split = "highest_rate",
                effect = "reduce_term", penalty = "0",
            }),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("projection.invalid_field", Str(problem, "code"));
        Assert.Contains(List(problem, "errors"), e => Str(e, "path") == "events[0].amount" && Str(e, "code") == "projection.invalid_field");
    }

    [Fact]
    public async Task Simulate_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var livret = await ValuedEnvelopeAsync(app, "100000.00");
        var purchase = await PurchaseAsync(app, livret);
        var before = await app.GetRawAsync(app.AdultA, "/api/v1/scenarios");

        await SimulateAsync(app, Document(events: new
        {
            id = Guid.NewGuid(), kind = "property_purchase", start = new { on = "2026-01-10" }, scenario = purchase,
        }));

        Assert.Equal(before, await app.GetRawAsync(app.AdultA, "/api/v1/scenarios"));
        Assert.Single(List(JsonDocument.Parse(before).RootElement, "items"));
    }

    private static async Task DepartureAsync(NidApp app, string? birthDate, int age = 64)
        => (await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/core/persons/{app.AdultA.Value}/retirement", new { birthDate, retirementAge = age, retirementMonths = 0 }))
            .EnsureSuccessStatusCode();

    private static async Task AssumptionsAsync(NidApp app, string savings, string? livingCost = null)
        => (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/core/projection-assumptions", new
        {
            classRates = AllocationClasses.All.ToDictionary(c => c, _ => "0"), inflation = "0", monthlySavings = savings, savingsClass = "cash",
            freedPaymentsToSavings = true, incomeGrowth = "0", withdrawalRate = "4", monthlyLivingCost = livingCost ?? "0",
        })).EnsureSuccessStatusCode();

    private static object Departing(NidApp app, string horizon, int years, string? pension = null)
        => new
        {
            version = 1, horizon, assumptions = new { }, events = Array.Empty<object>(),
            retirements = new[] { new { person = app.AdultA.Value, years, months = 0, monthlyPension = pension } },
        };

    [Fact]
    public async Task An_earlier_departure_opens_the_per_and_moves_the_milestone() // retirement spec §2
    {
        await using var app = await NidApp.StartAsync(fixture);
        await DepartureAsync(app, "1961-11-10", 75); // the household leaves in 2036-11: the PER stays locked in the 5 years
        await ValuedEnvelopeAsync(app, "1000.00");
        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = "PER", kind = "per", currency = "EUR", visibility = "shared", owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        });
        created.EnsureSuccessStatusCode();
        var perId = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("id").GetGuid();
        await AssumptionsAsync(app, "-500");
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{perId}/valuations",
            new { asOf = "2025-09-30", amount = new { amount = "50000.00", currency = "EUR" }, note = (string?)null })).EnsureSuccessStatusCode();

        var view = await SimulateAsync(app, Departing(app, "5y", 64)); // 2025-11: the PER opens in month 2

        Assert.Contains(Warnings(view.GetProperty("base")), w => w.Code == "savings_exhausted");
        Assert.DoesNotContain(Warnings(view.GetProperty("scenario")), w => w.Code == "savings_exhausted");
        Assert.DoesNotContain(List(view.GetProperty("base"), "milestones"), m => Str(m, "kind") == "retirement");
        Assert.Equal("2025-11-30", Str(Assert.Single(List(view.GetProperty("scenario"), "milestones"), m => Str(m, "kind") == "retirement"), "on"));
        var echoed = Assert.Single(List(view.GetProperty("document"), "retirements"));
        Assert.Equal((app.AdultA.Value, 64), (echoed.GetProperty("person").GetGuid(), echoed.GetProperty("years").GetInt32()));
    }

    [Fact]
    public async Task A_retire_horizon_ends_at_the_scenarios_departure() // retirement spec §2
    {
        await using var app = await NidApp.StartAsync(fixture);
        await DepartureAsync(app, "1990-05-10");

        var view = await SimulateAsync(app, Departing(app, $"retire:{app.AdultA.Value}", 60));

        Assert.Equal("2050-05-31", Str(Points(view, "scenario")[^1], "on"));
        Assert.Equal("2050-05-31", Str(Points(view, "base")[^1], "on")); // one grid
    }

    [Fact]
    public async Task A_departure_without_a_birth_date_is_ignored_and_warned() // retirement spec §2
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ValuedEnvelopeAsync(app, "10000.00");

        var view = await SimulateAsync(app, Departing(app, "10y", 60, "1500"));

        Assert.Equal(Raw(Points(view, "base")), Raw(Points(view, "scenario")));
        Assert.Equal([("retirement_undated", "1")], Warnings(view.GetProperty("scenario")));
    }

    private static async Task PensionAsync(NidApp app, string amount)
        => (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/income/sources", new Dictionary<string, object?>
        {
            ["displayName"] = "Retraite", ["kind"] = "pension", ["currency"] = "EUR", ["visibility"] = "shared",
            ["owners"] = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            ["phases"] = new[] { new { label = "Retraite", startsOn = "2045-01-01", netAmount = new { amount, currency = "EUR" }, every = new { count = 1, unit = "month" } } },
        })).EnsureSuccessStatusCode();

    [Fact]
    public async Task A_monthly_pension_replaces_the_persons_own_from_the_departure() // retirement spec §2
    {
        await using var app = await NidApp.StartAsync(fixture);
        await DepartureAsync(app, "1963-10-10"); // leaves in 2027-10: the switch month is 25
        await ValuedEnvelopeAsync(app, "100000.00"); // 4 % of it covers 333.33 a month
        await AssumptionsAsync(app, "0", "2000");
        await PensionAsync(app, "1800");

        var view = await SimulateAsync(app, Departing(app, "20y", 64, "1500")); // 1 833.33 from 2027-11, in place of the 1 800

        Assert.Equal("2027-11", Str(view.GetProperty("base"), "financialIndependence")); // 2 133.33 from the switch month
        // Replaced, not added: 1 500 + 333.33 no longer covers 2 000 at the switch month, so the date moves later (it stays 2027-11 if the 1 800 were kept).
        Assert.True(string.CompareOrdinal(Str(view.GetProperty("scenario"), "financialIndependence"), "2027-11") > 0);
    }

    [Fact]
    public async Task A_monthly_pension_is_added_when_the_person_has_none() // retirement spec §2
    {
        await using var app = await NidApp.StartAsync(fixture);
        await DepartureAsync(app, "1963-10-10");
        await ValuedEnvelopeAsync(app, "100000.00");
        await AssumptionsAsync(app, "0", "2000");

        var view = await SimulateAsync(app, Departing(app, "10y", 64, "1700")); // 2 033.33 from the switch month 25 (2027-11)

        Assert.Equal(JsonValueKind.Null, view.GetProperty("base").GetProperty("financialIndependence").ValueKind);
        Assert.Equal("2027-11", Str(view.GetProperty("scenario"), "financialIndependence"));
    }
}
