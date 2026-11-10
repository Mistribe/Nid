using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.Core.IntegrationTests.Projection;

/// <summary>
/// Net worth projection spec §4.5, §6 and §7: a saved projection scenario, <c>POST /api/v1/scenarios/projection</c>,
/// <c>GET</c>/<c>PUT /api/v1/scenarios/projection/{id}</c>, listed and deleted with the purchases. Today is 2025-10-15.
/// </summary>
public class ProjectionScenarioEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Url = "/api/v1/scenarios/projection";

    private static object Document(string horizon = "10y", params object[] events)
        => new { version = 1, horizon, assumptions = new { }, events };

    private static object Income(string label = "Retraite", string amount = "-1800")
        => new { id = Guid.NewGuid(), kind = "income_change", label, start = new { on = "2030-01-01" }, monthlyAmount = amount };

    private static object Repayment(Guid loan, string on, string amount)
        => new { id = Guid.NewGuid(), kind = "early_repayment", start = new { on }, amount, split = "chosen", loan, effect = "reduce_term", penalty = "0" };

    private static object Body(NidApp app, object document, string name = "Retraite anticipée", string visibility = "shared", params PersonId[] owners)
        => Scoped(app, "household", document, name, visibility, owners);

    private static object Scoped(NidApp app, string scope, object document, string name, string visibility, params PersonId[] owners)
        => new
        {
            name, visibility, scope,
            owners = (owners.Length == 0 ? [app.AdultA] : owners).Select(p => new { personId = p.Value, shareBp = 10_000 / Math.Max(owners.Length, 1) }).ToArray(),
            document,
        };

    private static async Task<HttpResponseMessage> CreateAsync(NidApp app, object body, PersonId? actor = null)
        => await app.RawAsync(actor ?? app.AdultA, HttpMethod.Post, Url, body);

    private static async Task<Guid> SavedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<JsonElement> GetAsync(NidApp app, Guid id, PersonId? actor = null)
        => await OkAsync(await app.RawAsync(actor ?? app.AdultA, HttpMethod.Get, $"{Url}/{id}"));

    private static async Task<JsonElement> SimulateAsync(NidApp app, object document, string scope = "household")
        => await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url}/simulate", new { scope, document }));

    private static List<JsonElement> List(JsonElement view, string name) => view.GetProperty(name).EnumerateArray().ToList();

    private static string? Str(JsonElement element, string name) => element.GetProperty(name).GetString();

    private static List<string> Raw(IEnumerable<JsonElement> points) => points.Select(p => p.GetRawText()).ToList();

    private static List<JsonElement> Points(JsonElement result, string series) => List(result.GetProperty(series), "points");

    private static async Task<Guid> PurchaseAsync(NidApp app)
        => await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/scenarios", new
        {
            name = "Appartement", visibility = "shared", scope = "household", owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            document = new
            {
                version = 1, use = "primary", purchaseOn = "2026-01-10", price = "215000", notaryFees = "15000", currentRent = "800", monthlyCharges = "100",
                funding = Array.Empty<object>(),
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
        }));

    [Fact]
    public async Task Creates_reads_and_replaces_a_projection_scenario()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var document = Document("10y", Income());

        var id = await SavedAsync(await CreateAsync(app, Body(app, document)));
        var view = await GetAsync(app, id);

        Assert.Equal((id, "Retraite anticipée", "shared", "household", true),
            (view.GetProperty("id").GetGuid(), Str(view, "name"), Str(view, "visibility"), Str(view, "scope"), view.GetProperty("canWrite").GetBoolean()));
        var simulated = await SimulateAsync(app, document);
        var result = view.GetProperty("result");
        Assert.Equal(Str(simulated, "gapNominal"), Str(result, "gapNominal"));
        Assert.Equal(Str(simulated, "gapReal"), Str(result, "gapReal"));
        Assert.Equal(Raw(Points(simulated, "scenario")), Raw(Points(result, "scenario")));
        var echoed = Assert.Single(List(result.GetProperty("document"), "events"));
        Assert.Equal(("income_change", "-1800.00"), (Str(echoed, "kind"), Str(echoed, "monthlyAmount")));

        var replaced = await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url}/{id}",
            Body(app, Document("20y", Income(), Income("Temps partiel", "-400")), "Temps partiel", "private")));

        Assert.Equal(("Temps partiel", "private"), (Str(replaced, "name"), Str(replaced, "visibility")));
        Assert.Equal("20y", Str(replaced.GetProperty("result").GetProperty("document"), "horizon"));
        Assert.Equal(2, List(replaced.GetProperty("result").GetProperty("document"), "events").Count);
        var again = await GetAsync(app, id);
        Assert.Equal(replaced.GetRawText(), again.GetRawText());
    }

    [Fact]
    public async Task Scenario_ranges_on_demand_with_its_own_volatility() // ranges spec §2, §4
    {
        await using var app = await NidApp.StartAsync(fixture);
        var document = new
        {
            version = 1, horizon = "5y", events = Array.Empty<object>(),
            assumptions = new { classVolatility = new { equity = "25" } },
        };
        var id = await SavedAsync(await CreateAsync(app, Body(app, document)));

        var plain = (await GetAsync(app, id)).GetProperty("result");
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("scenario").GetProperty("ranges").ValueKind);
        Assert.Equal("25", plain.GetProperty("document").GetProperty("assumptions").GetProperty("classVolatility").GetProperty("equity").GetString());
        Assert.Equal("25", plain.GetProperty("scenario").GetProperty("assumptions").GetProperty("classVolatility").GetProperty("equity").GetString());

        var result = (await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url}/{id}?ranges=true"))).GetProperty("result");
        Assert.Equal(JsonValueKind.Null, result.GetProperty("base").GetProperty("ranges").ValueKind);
        var ranges = result.GetProperty("scenario").GetProperty("ranges");
        Assert.Equal(Points(result, "scenario").Count, List(ranges, "points").Count);
        Assert.Equal(1000, ranges.GetProperty("runs").GetInt32());
    }

    [Fact]
    public async Task Scenario_overrides_round_trip_the_yearly_tables()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var document = new
        {
            version = 1, horizon = "10y", events = Array.Empty<object>(),
            assumptions = new
            {
                inflationByYear = new Dictionary<string, string> { ["2026"] = "2.5" },
                classRatesByYear = new { cash = new Dictionary<string, string> { ["2027"] = "3" } }, incomeGrowth = "0.5", withdrawalRate = "3.5", monthlyLivingCost = "1800.00",
            },
        };

        var id = await SavedAsync(await CreateAsync(app, Body(app, document)));

        var echoed = (await GetAsync(app, id)).GetProperty("result").GetProperty("document").GetProperty("assumptions");
        Assert.Equal("""{"2026":"2.5"}""", echoed.GetProperty("inflationByYear").GetRawText());
        Assert.Equal("""{"cash":{"2027":"3"}}""", echoed.GetProperty("classRatesByYear").GetRawText());
        Assert.Equal("0.5", echoed.GetProperty("incomeGrowth").GetString());
        Assert.Equal(("3.5", "1800.00"), (echoed.GetProperty("withdrawalRate").GetString(), echoed.GetProperty("monthlyLivingCost").GetString()));
    }

    [Fact]
    public async Task Scenario_saved_with_a_past_override_year_drops_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var document = new
        {
            version = 1, horizon = "10y", events = Array.Empty<object>(),
            assumptions = new { inflationByYear = new Dictionary<string, string> { ["2024"] = "9", ["2026"] = "2.5" } },
        };

        var id = await SavedAsync(await CreateAsync(app, Body(app, document)));

        var echoed = (await GetAsync(app, id)).GetProperty("result").GetProperty("document").GetProperty("assumptions");
        Assert.Equal("""{"2026":"2.5"}""", echoed.GetProperty("inflationByYear").GetRawText());
    }

    [Fact]
    public async Task Stored_past_override_year_is_not_echoed_on_read()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await SavedAsync(await CreateAsync(app, Body(app, Document())));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var store = scope.ServiceProvider.GetRequiredService<IScenarioStore>();
            var stored = (await store.FindAsync(new ItemId(id), TestContext.Current.CancellationToken))!;
            var past = new Dictionary<int, decimal> { [2024] = 0.09m, [2026] = 0.025m };
            await store.SaveAsync(stored with { Projection = stored.Projection! with { Assumptions = stored.Projection.Assumptions with { InflationByYear = past } } },
                TestContext.Current.CancellationToken);
        }

        var echoed = (await GetAsync(app, id)).GetProperty("result").GetProperty("document").GetProperty("assumptions");

        Assert.Equal("""{"2026":"2.5"}""", echoed.GetProperty("inflationByYear").GetRawText());
    }

    [Fact]
    public async Task List_shows_both_kinds_with_the_projection_gap()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PurchaseAsync(app);
        var document = Document("10y", Income());
        var id = await SavedAsync(await CreateAsync(app, Body(app, document, "Projection retraite")));

        var list = List(await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/scenarios")), "items");

        Assert.Equal(2, list.Count);
        var purchase = Assert.Single(list, i => Str(i, "kind") == "property_purchase");
        Assert.Equal(("Appartement", "primary"), (Str(purchase, "name"), Str(purchase, "use")));
        Assert.Equal("215000.00", Str(purchase.GetProperty("price"), "amount"));
        var projection = Assert.Single(list, i => Str(i, "kind") == "projection");
        Assert.Equal(id, projection.GetProperty("id").GetGuid());
        Assert.Equal("Projection retraite", Str(projection, "name"));
        foreach (var field in new[] { "use", "price", "newPayment", "debtRatioAfter" })
            Assert.Equal(JsonValueKind.Null, projection.GetProperty(field).ValueKind);
        var simulated = await SimulateAsync(app, document);
        Assert.Equal((Str(simulated, "gapNominal"), "EUR"), (Str(projection.GetProperty("gap"), "amount"), Str(projection.GetProperty("gap"), "currency")));
        Assert.Equal(app.AdultA.Value, Assert.Single(List(projection, "owners")).GetProperty("personId").GetGuid());
    }

    [Fact]
    public async Task Unknown_loan_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await CreateAsync(app, Body(app, Document("10y", Repayment(Guid.NewGuid(), "2026-01-20", "1000"))));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "projection.target_not_found", "events[0].loan"), await RefusalAsync(response));
        Assert.Empty(List(await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/scenarios")), "items"));
    }

    [Fact]
    public async Task Scenario_withdrawal_rate_of_zero_is_refused_at_write() // retirement spec §3
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await CreateAsync(app, Body(app, new
        {
            version = 1, horizon = "10y", events = Array.Empty<object>(), assumptions = new { withdrawalRate = "0" },
        }));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "projection.invalid_field", "assumptions.withdrawalRate"), await RefusalAsync(response));
    }

    private static object WithRetirements(params object[] retirements)
        => new { version = 1, horizon = "10y", events = Array.Empty<object>(), assumptions = new { }, retirements };

    [Fact]
    public async Task Departures_round_trip_and_an_absent_list_reads_empty() // retirement spec §2
    {
        await using var app = await NidApp.StartAsync(fixture);
        var departure = new { person = app.AdultA.Value, years = 60, months = 6, monthlyPension = "1500" };

        var id = await SavedAsync(await CreateAsync(app, Body(app, WithRetirements(departure))));
        var plain = await SavedAsync(await CreateAsync(app, Body(app, Document("10y", Income()))));

        var read = Assert.Single(List((await GetAsync(app, id)).GetProperty("result").GetProperty("document"), "retirements"));
        Assert.Equal((app.AdultA.Value, 60, 6, "1500.00"),
            (read.GetProperty("person").GetGuid(), read.GetProperty("years").GetInt32(), read.GetProperty("months").GetInt32(), Str(read, "monthlyPension")));
        Assert.Empty(List((await GetAsync(app, plain)).GetProperty("result").GetProperty("document"), "retirements"));
    }

    [Theory]
    [InlineData(49, 0, "-1", "retirements[0].years")]
    [InlineData(76, 0, "-1", "retirements[0].years")]
    [InlineData(60, 12, "-1", "retirements[0].months")]
    [InlineData(60, 0, "-5", "retirements[0].monthlyPension")]
    public async Task Departure_out_of_bounds_is_refused_at_write(int years, int months, string pension, string field)
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await CreateAsync(app, Body(app, WithRetirements(new { person = app.AdultA.Value, years, months, monthlyPension = pension == "-1" ? null : pension })));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "projection.invalid_field", field), await RefusalAsync(response));
    }

    [Fact]
    public async Task Two_departures_of_one_person_are_refused_at_write()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var departure = new { person = app.AdultA.Value, years = 60, months = 0 };

        var response = await CreateAsync(app, Body(app, WithRetirements(departure, departure)));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "projection.invalid_field", "retirements[1].person"), await RefusalAsync(response));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Departure_of_an_unknown_person_or_a_dependent_is_refused_at_write(bool dependent)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var person = dependent ? app.Dependent.Value : Guid.NewGuid();

        var response = await CreateAsync(app, Body(app, WithRetirements(new { person, years = 60, months = 0 })));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "projection.target_not_found", "retirements[0].person"), await RefusalAsync(response));
    }

    [Fact]
    public async Task Repayment_above_balance_is_refused_at_write()
    {
        await using var app = await NidApp.StartAsync(fixture);
        // 12 000 over 12 payments from 2025-01-15: 2 000 still owed after the 15 October payment.
        var loan = await NewAsync(app, Short(app));

        var above = await CreateAsync(app, Body(app, Document("10y", Repayment(loan, "2025-10-20", "5000"))));
        var within = await CreateAsync(app, Body(app, Document("10y", Repayment(loan, "2025-10-20", "1500"))));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "projection.invalid_field", "events[0].amount"), await RefusalAsync(above));
        await SavedAsync(within);
    }

    [Fact]
    public async Task Repayment_on_a_loan_sold_earlier_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{home}/valuations", new { asOf = "2025-06-01", amount = "300000" }));
        var body = Golden(app);
        body["propertyId"] = home;
        var loan = await NewAsync(app, body);
        object Sale(string on) => new { id = Guid.NewGuid(), kind = "property_sale", start = new { on }, property = home, price = "320000", costs = "0" };

        // The repayment comes first in the document but after the sale in time.
        var after = await CreateAsync(app, Body(app, Document("10y", Repayment(loan, "2026-06-01", "10000"), Sale("2026-03-01"))));
        var before = await CreateAsync(app, Body(app, Document("10y", Repayment(loan, "2026-01-20", "10000"), Sale("2026-03-01"))));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "projection.invalid_field", "events[0].loan"), await RefusalAsync(after));
        await SavedAsync(before);
    }

    [Fact]
    public async Task Event_on_a_private_item_is_hidden_from_the_other_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        // B's private loan, seen by B in their own scope (« me ») only; the scenario is computed in that scope for each reader.
        var secret = await NewAsync(app, Golden(app, "Prêt secret", "private", app.AdultB), app.AdultB);
        var visible = Income();
        var id = await SavedAsync(await CreateAsync(app,
            Scoped(app, "me", Document("10y", visible, Repayment(secret, "2025-11-20", "4321")), "Commun", "shared", app.AdultA, app.AdultB), app.AdultB));

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url}/{id}");
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(secret.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4321", text);
        Assert.DoesNotContain("Prêt secret", text);
        var result = JsonDocument.Parse(text).RootElement.GetProperty("result");
        Assert.Contains(List(result.GetProperty("scenario"), "warnings"), w => Str(w, "code") == "hidden_targets" && Str(w, "detail") == "1");
        var without = await SimulateAsync(app, Document("10y", visible), "me");
        Assert.Equal(Raw(Points(without, "scenario")), Raw(Points(result, "scenario")));
        Assert.Equal(Str(without, "gapNominal"), Str(result, "gapNominal"));
        var shown = Assert.Single(List(result.GetProperty("document"), "events"));

        // A replaces with what they were shown, renamed: B's event is kept.
        var replaced = await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url}/{id}", new
        {
            name = "Commun renommé", visibility = "shared", scope = "me", document = result.GetProperty("document"),
        }));
        Assert.DoesNotContain(secret.ToString(), replaced.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(shown.GetRawText(), Assert.Single(List(replaced.GetProperty("result").GetProperty("document"), "events")).GetRawText());

        // A replace A gets refused names nothing of B's.
        var refused = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url}/{id}", Scoped(app, "me",
            Document("10y", visible, Repayment(Guid.NewGuid(), "2025-11-20", "100")), "Commun renommé", "shared"));
        var refusal = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.DoesNotContain(secret.ToString(), refusal, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4321", refusal);
        Assert.DoesNotContain("Prêt secret", refusal);
        Assert.Contains("events[1].loan", refusal);

        var seenByB = await GetAsync(app, id, app.AdultB);
        var events = List(seenByB.GetProperty("result").GetProperty("document"), "events");
        Assert.Equal("Commun renommé", Str(seenByB, "name"));
        Assert.Equal(["income_change", "early_repayment"], events.Select(e => Str(e, "kind")));
        Assert.Equal((secret, "4321.00"), (events[1].GetProperty("loan").GetGuid(), Str(events[1], "amount")));
        Assert.DoesNotContain(List(seenByB.GetProperty("result").GetProperty("scenario"), "warnings"), w => Str(w, "code") == "hidden_targets");
    }

    [Fact]
    public async Task Replace_drops_an_event_whose_loan_is_archived()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await NewAsync(app, Golden(app));
        var id = await SavedAsync(await CreateAsync(app, Body(app, Document("10y", Income(), Repayment(loan, "2026-01-20", "1000")))));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            (await scope.ServiceProvider.GetRequiredService<IItemRegistry>().ArchiveAsync(new ItemId(loan), NodaTime.SystemClock.Instance.GetCurrentInstant(), TestContext.Current.CancellationToken))
                .Ok();
        }

        // Nobody sees the archived loan any more: the event is hidden from its own writer, then dropped by their replace.
        var shown = (await GetAsync(app, id)).GetProperty("result");
        Assert.Contains(List(shown.GetProperty("scenario"), "warnings"), w => Str(w, "code") == "hidden_targets" && Str(w, "detail") == "1");
        await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url}/{id}", new
        {
            name = "Retraite anticipée", visibility = "shared", scope = "household", document = shown.GetProperty("document"),
        }));

        var after = (await GetAsync(app, id)).GetProperty("result");
        Assert.DoesNotContain(List(after.GetProperty("scenario"), "warnings"), w => Str(w, "code") == "hidden_targets");
        Assert.Equal("income_change", Str(Assert.Single(List(after.GetProperty("document"), "events")), "kind"));
    }

    [Theory]
    [InlineData("start", "events[0].start.on")]
    [InlineData("until", "events[0].until.on")]
    public async Task Event_date_beyond_forty_years_is_refused(string which, string path)
    {
        await using var app = await NidApp.StartAsync(fixture);
        object far = new { on = "9999-12-31" };
        object near = new { on = "2030-01-01" };
        object ev = new { id = Guid.NewGuid(), kind = "income_change", start = which == "start" ? far : near, until = which == "start" ? null : far, monthlyAmount = "100" };

        var response = await CreateAsync(app, Body(app, Document("10y", ev)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("projection.invalid_field", Str(problem, "code"));
        Assert.Contains(List(problem, "errors"), e => Str(e, "path") == path);
    }

    [Fact]
    public async Task Purchase_id_on_projection_routes_is_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var purchase = await PurchaseAsync(app);
        var projection = await SavedAsync(await CreateAsync(app, Body(app, Document("10y", Income()))));

        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url}/{purchase}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url}/{purchase}",
            Body(app, Document("10y")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/scenarios/{projection}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url}/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Delete_archives_a_projection_scenario()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await SavedAsync(await CreateAsync(app, Body(app, Document("10y", Income()))));

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"/api/v1/scenarios/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url}/{id}")).StatusCode);
        Assert.Empty(List(await OkAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/scenarios")), "items"));
    }

    // Spec §14: GET /api/v1/scenarios/compare, the base and 1 to 3 scenarios on the request's grid and scope.

    private static Task<HttpResponseMessage> CompareRawAsync(NidApp app, string ids, string? horizon = null, string scope = "household", PersonId? actor = null)
        => app.RawAsync(actor ?? app.AdultA, HttpMethod.Get,
            $"/api/v1/scenarios/compare?scope={scope}&ids={ids}{(horizon is null ? "" : $"&horizon={horizon}")}");

    private static async Task<JsonElement> CompareAsync(NidApp app, string ids, string? horizon = null, string scope = "household")
        => await OkAsync(await CompareRawAsync(app, ids, horizon, scope));

    private static decimal Dec(JsonElement element, string name) => decimal.Parse(Str(element, name)!, System.Globalization.CultureInfo.InvariantCulture);

    private static object PurchaseEventOn(Guid scenario)
        => new { id = Guid.NewGuid(), kind = "property_purchase", start = new { on = "2025-10-15" }, scenario };

    [Fact]
    public async Task Compare_returns_the_base_once_and_each_scenario_in_ids_order()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var purchase = await PurchaseAsync(app);
        var projection = await SavedAsync(await CreateAsync(app, Body(app, Document("10y", Income()))));

        var view = await CompareAsync(app, $"{projection},{purchase}");

        var scenarios = List(view, "scenarios");
        Assert.Equal([(projection, "projection", "Retraite anticipée"), (purchase, "property_purchase", "Appartement")],
            scenarios.Select(s => (s.GetProperty("id").GetGuid(), Str(s, "kind"), Str(s, "name"))));
        var grid = List(view.GetProperty("base"), "points").Select(p => Str(p, "on")).ToList();
        foreach (var scenario in scenarios)
        {
            var points = List(scenario.GetProperty("result"), "points");
            Assert.Equal(grid, points.Select(p => Str(p, "on")));
            Assert.Equal(Dec(points[^1], "nominal") - Dec(List(view.GetProperty("base"), "points")[^1], "nominal"), Dec(scenario, "gapNominal"));
        }
        Assert.NotEqual(0m, Dec(scenarios[0], "gapNominal"));
    }

    [Fact]
    public async Task Compare_a_purchase_equals_a_projection_with_its_single_purchase_event()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var purchase = await PurchaseAsync(app);
        var projection = await SavedAsync(await CreateAsync(app, Body(app, Document("10y", PurchaseEventOn(purchase)))));

        var scenarios = List(await CompareAsync(app, $"{purchase},{projection}"), "scenarios");

        Assert.Equal(Raw(List(scenarios[1].GetProperty("result"), "points")), Raw(List(scenarios[0].GetProperty("result"), "points")));
        Assert.Equal(Str(scenarios[1], "gapNominal"), Str(scenarios[0], "gapNominal"));
    }

    [Fact]
    public async Task Compare_uses_the_request_horizon_not_the_stored_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var projection = await SavedAsync(await CreateAsync(app, Body(app, Document("20y", Income()))));

        var view = await CompareAsync(app, $"{projection}", "5y");

        var result = Assert.Single(List(view, "scenarios")).GetProperty("result");
        Assert.Equal(("5y", "5y"), (Str(view.GetProperty("base"), "horizon"), Str(result, "horizon")));
        Assert.Equal(Str(List(view.GetProperty("base"), "points")[^1], "on"), Str(List(result, "points")[^1], "on"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public async Task Compare_refuses_ids_out_of_bounds(int count)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var one = await SavedAsync(await CreateAsync(app, Body(app, Document("10y", Income()))));
        var ids = count switch
        {
            0 => "",
            -1 => $"{one},{one}",
            _ => string.Join(',', Enumerable.Range(0, count).Select(_ => Guid.NewGuid())),
        };

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "projection.invalid_field", "ids"), await RefusalAsync(await CompareRawAsync(app, ids)));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("deleted")]
    [InlineData("private of B")]
    public async Task Compare_answers_404_for_an_unknown_a_deleted_or_another_adults_private_scenario(string which)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await SavedAsync(await CreateAsync(app, Body(app, Document("10y", Income()))));
        var target = which switch
        {
            "unknown" => Guid.NewGuid(),
            "deleted" => shared,
            _ => await SavedAsync(await CreateAsync(app, Body(app, Document("10y", Income()), "Secret", "private", app.AdultB), app.AdultB)),
        };
        if (which == "deleted")
            Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"/api/v1/scenarios/{shared}")).StatusCode);

        var response = await CompareRawAsync(app, $"{target}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Secret", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Compare_answers_404_for_my_private_scenario_in_household_scope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await SavedAsync(await CreateAsync(app, Scoped(app, "me", Document("10y", Income()), "À moi", "private")));

        Assert.Equal(HttpStatusCode.NotFound, (await CompareRawAsync(app, $"{mine}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CompareRawAsync(app, $"{mine}", scope: "me")).StatusCode);
    }

    [Fact]
    public async Task Compare_does_not_repeat_the_base_warnings_under_a_scenario()
    {
        await using var app = await NidApp.StartAsync(fixture);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/income/sources", new Dictionary<string, object?>
        {
            ["displayName"] = "Mon CDD", ["kind"] = "salary", ["currency"] = "EUR", ["visibility"] = "shared",
            ["owners"] = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            ["phases"] = new[]
            {
                new
                {
                    label = "CDD", startsOn = "2025-01-01", endsOn = "2026-03-01", netAmount = new { amount = "2000", currency = "EUR" },
                    every = new { count = 1, unit = "month" },
                },
            },
        })).EnsureSuccessStatusCode();
        var projection = await SavedAsync(await CreateAsync(app, Body(app, Document("10y", Income()))));

        var view = await CompareAsync(app, $"{projection}");

        Assert.Contains(List(view.GetProperty("base"), "warnings"), w => Str(w, "code") == "income_end_extended");
        Assert.DoesNotContain(List(Assert.Single(List(view, "scenarios")).GetProperty("result"), "warnings"), w => Str(w, "code") == "income_end_extended");
    }

    [Fact]
    public async Task A_scenario_that_saves_more_reaches_independence_earlier() // retirement spec §3, on the scenario and compare views
    {
        await using var app = await NidApp.StartAsync(fixture);
        (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/core/projection-assumptions", new
        {
            classRates = AllocationClasses.All.ToDictionary(c => c, _ => "0"), inflation = "0", monthlySavings = "2500", savingsClass = "cash",
            freedPaymentsToSavings = true, withdrawalRate = "4", monthlyLivingCost = "2000",
        })).EnsureSuccessStatusCode(); // 600 000 of capital are needed: 240 months at 2 500, 120 at 5 000
        var document = new { version = 1, horizon = "20y", events = Array.Empty<object>(), assumptions = new { monthlySavings = "5000" } };
        var id = await SavedAsync(await CreateAsync(app, Body(app, document)));

        var result = (await GetAsync(app, id)).GetProperty("result");
        var compared = Assert.Single(List(await CompareAsync(app, $"{id}"), "scenarios"));

        Assert.Equal(("2045-10", "2035-10"), (Str(result.GetProperty("base"), "financialIndependence"), Str(result.GetProperty("scenario"), "financialIndependence")));
        Assert.Equal("2035-10", Str(compared.GetProperty("result"), "financialIndependence"));
    }

    [Fact]
    public async Task Selling_a_let_property_takes_its_rent_out_of_the_independence() // retirement spec §3: the sale's rent no longer covers the living cost
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{home}/valuations", new { asOf = "2025-06-01", amount = "100000" }));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{home}/tenancies",
            new { label = "Bail", startsOn = "2025-01-01", endsOn = (string?)null, rentMonthly = "800", chargesMonthly = "0" }));
        (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/core/projection-assumptions", new
        {
            classRates = AllocationClasses.All.ToDictionary(c => c, _ => "0"), inflation = "0", monthlySavings = "1000", savingsClass = "cash",
            freedPaymentsToSavings = true, withdrawalRate = "4", monthlyLivingCost = "1200",
        })).EnsureSuccessStatusCode(); // rent 800 + 4 % of 120 000 cover 1 200: 120 months of savings at 1 000 (2035-10)
        var sale = new { id = Guid.NewGuid(), kind = "property_sale", start = new { on = "2026-03-01" }, property = home, price = "100000", costs = "0" };
        var id = await SavedAsync(await CreateAsync(app, Body(app, Document("20y", sale))));

        var result = (await GetAsync(app, id)).GetProperty("result");

        Assert.Equal("2035-10", Str(result.GetProperty("base"), "financialIndependence"));
        // Without its rent the sold home leaves 360 000 of capital to build from 100 000 and 200 a month: beyond the horizon.
        Assert.Equal(JsonValueKind.Null, result.GetProperty("scenario").GetProperty("financialIndependence").ValueKind);
    }

    [Fact]
    public async Task Compare_on_a_retire_horizon_uses_the_households_departure_for_every_scenario() // retirement spec §2
    {
        await using var app = await NidApp.StartAsync(fixture);
        (await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/core/persons/{app.AdultA.Value}/retirement",
            new { birthDate = "1990-05-10", retirementAge = 64, retirementMonths = 0 })).EnsureSuccessStatusCode(); // the household: 2054-05
        var ids = new List<Guid>();
        foreach (var years in new[] { 58, 60 })
            ids.Add(await SavedAsync(await CreateAsync(app, Body(app, WithRetirements(new { person = app.AdultA.Value, years, months = 0 }), $"A {years}"))));

        var view = await CompareAsync(app, string.Join(',', ids), $"retire:{app.AdultA.Value}");

        Assert.Equal("2054-05-31", Str(List(view.GetProperty("base"), "points")[^1], "on"));
        foreach (var scenario in List(view, "scenarios"))
            Assert.Equal("2054-05-31", Str(List(scenario.GetProperty("result"), "points")[^1], "on"));
    }
}
