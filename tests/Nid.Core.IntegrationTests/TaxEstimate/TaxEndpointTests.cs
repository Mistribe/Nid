using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application.Tax;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Core.IntegrationTests.TaxEstimate;

/// <summary>Tax estimate spec §6 to §8, through the real API.</summary>
public class TaxEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<JsonElement> EstimateAsync(NidApp app, PersonId reader, int year, string scope = "me")
        => JsonDocument.Parse(await app.GetRawAsync(reader, $"/api/v1/tax?year={year}&scope={scope}")).RootElement;

    private static async Task AddPhaseAsync(NidApp app, PersonId owner, ItemId source, string net, string gross)
        => (await app.RawAsync(owner, HttpMethod.Post, $"/api/v1/income/sources/{source.Value}/phases", new
        {
            label = "Salaire", startsOn = "2025-01-01", netAmount = new { amount = net, currency = "EUR" },
            every = new { count = 1, unit = "year" }, grossAmount = new { amount = gross, currency = "EUR" },
        })).EnsureSuccessStatusCode();

    private static decimal GrossOf(JsonElement estimate)
        => estimate.GetProperty("inputs").GetProperty("declarants").EnumerateArray()
            .Sum(d => decimal.Parse(d.GetProperty("salaryGross").GetString()!, CultureInfo.InvariantCulture));

    private static async Task<string> PerAsync(NidApp app, PersonId owner, string name, string openedOn, string openingOn, string visibility = "shared")
        => JsonDocument.Parse(await (await app.RawAsync(owner, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = name, kind = "per", currency = "EUR", visibility, openedOn,
            owners = new[] { new { personId = owner.Value, shareBp = 10_000 } },
            opening = new { occurredOn = openingOn, amount = new { amount = "1000.00", currency = "EUR" } },
        })).EnsureSuccessStatusCode().Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;

    /// <summary>Each declarant's PER payments, by adult: the declarants come in the order of <c>adults</c>.</summary>
    private static Dictionary<Guid, string> PerOf(JsonElement estimate)
        => estimate.GetProperty("adults").EnumerateArray().Zip(estimate.GetProperty("inputs").GetProperty("declarants").EnumerateArray())
            .ToDictionary(p => p.First.GetProperty("id").GetGuid(), p => p.Second.GetProperty("perContributions").GetString()!);

    /// <summary>Each declarant's result names its adult, in the order of <c>adults</c>.</summary>
    private static void AssertResultsNameTheirAdults(JsonElement estimate)
    {
        var adults = estimate.GetProperty("adults").EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ToList();
        var persons = estimate.GetProperty("result").GetProperty("declarants").EnumerateArray().Select(d => d.GetProperty("person").GetGuid()).ToList();
        Assert.Equal(adults, persons);
    }

    [Fact]
    public async Task The_gross_of_the_year_feeds_the_estimate_and_the_other_adults_private_salary_stays_out()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await app.NewIncomeSourceAsync("Salaire A", app.AdultA);
        await AddPhaseAsync(app, app.AdultA, mine, "39000", "50000");
        var theirs = await app.NewIncomeSourceAsync("Salaire B", app.AdultB, visibility: "private");
        await AddPhaseAsync(app, app.AdultB, theirs, "30000", "40000");

        var estimate = await EstimateAsync(app, app.AdultA, 2025);

        Assert.Equal(50_000m, GrossOf(estimate));
        Assert.Equal(90_000m, GrossOf(await EstimateAsync(app, app.AdultB, 2025)));
        Assert.DoesNotContain("Salaire B", estimate.GetRawText());
        Assert.Equal(2025, estimate.GetProperty("result").GetProperty("rulesYear").GetInt32());
        AssertResultsNameTheirAdults(estimate);
    }

    /// <summary>The position of an adult among the declarants (the order of <c>adults</c>, which the household's names do not settle).</summary>
    private static int IndexOf(JsonElement estimate, PersonId adult)
        => estimate.GetProperty("adults").EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ToList().IndexOf(adult.Value);

    private static async Task AddPaymentAsync(NidApp app, PersonId owner, ItemId source, string label, string on, string net, string? gross, string? taxable)
        => (await app.RawAsync(owner, HttpMethod.Post, $"/api/v1/income/sources/{source.Value}/phases", new
        {
            label, startsOn = on, netAmount = new { amount = net, currency = "EUR" }, every = new { count = 1, unit = "month" }, maxCycles = 1,
            grossAmount = gross is null ? null : new { amount = gross, currency = "EUR" },
            taxableAmount = taxable is null ? null : new { amount = taxable, currency = "EUR" },
        })).EnsureSuccessStatusCode();

    [Fact]
    public async Task The_observed_rate_comes_from_payments_with_both_gross_and_taxable()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await app.NewIncomeSourceAsync("Salaire A", app.AdultA);
        var none = await EstimateAsync(app, app.AdultA, 2025);
        Assert.False(none.GetProperty("observedRates").TryGetProperty(app.AdultA.Value.ToString(), out _)); // no payment: no rate

        await AddPaymentAsync(app, app.AdultA, source, "Janvier", "2025-01-31", "3800", "5000", "4000");
        await AddPaymentAsync(app, app.AdultA, source, "Février", "2025-02-28", "4600", "6000", "4800");
        await AddPaymentAsync(app, app.AdultA, source, "Mars", "2025-03-31", "1000", null, "1500"); // no gross: out of the rate

        var estimate = await EstimateAsync(app, app.AdultA, 2025);

        // (11 000 − 8 800 + 0.0284925 × 11 000) ÷ 11 000 = 0.2285 once rounded.
        Assert.Equal("22.85", estimate.GetProperty("observedRates").GetProperty(app.AdultA.Value.ToString()).GetString());
        Assert.DoesNotContain(estimate.GetProperty("notices").EnumerateArray(), n => n.GetProperty("code").GetString() == "contribution_rate_differs");

        // The entered income feeds the estimate: 4 000 + 4 800 + 1 500, nothing estimated from the gross.
        var at = IndexOf(estimate, app.AdultA);
        var result = estimate.GetProperty("result").GetProperty("declarants")[at];
        Assert.Equal("entered", result.GetProperty("method").GetString());
        Assert.Equal("10300.00", result.GetProperty("netTaxableIncome").GetString());
        Assert.Equal("10300.00", estimate.GetProperty("inputs").GetProperty("declarants")[at].GetProperty("salaryTaxable").GetString());
        var detail = estimate.GetProperty("salaryDetail").EnumerateArray().ToList();
        Assert.Equal(["Janvier", "Février", "Mars"], detail.Select(d => d.GetProperty("note").GetString()));
        Assert.All(detail, d => Assert.Equal("entered", d.GetProperty("category").GetString()));
        Assert.Equal(["4000.00", "4800.00", "1500.00"], detail.Select(d => d.GetProperty("amount").GetString()));

        var saved = await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/tax/profile/2025?scope=me", new
        {
            pinel = Array.Empty<object>(), contributionRates = new Dictionary<string, string> { [app.AdultA.Value.ToString()] = "6.4" },
        });
        var notice = Assert.Single(JsonDocument.Parse(await saved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("notices").EnumerateArray(),
            n => n.GetProperty("code").GetString() == "contribution_rate_differs");
        Assert.Equal("Salaire A", notice.GetProperty("name").GetString());
        Assert.Equal("22.85", notice.GetProperty("amount").GetString());
    }

    [Fact]
    public async Task A_gross_alone_is_estimated_and_the_simulation_reads_the_entered_income_back()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await app.NewIncomeSourceAsync("Salaire A", app.AdultA);
        await AddPhaseAsync(app, app.AdultA, source, "39000", "50000");

        var estimate = await EstimateAsync(app, app.AdultA, 2025);

        var at = IndexOf(estimate, app.AdultA);
        Assert.Equal("estimated", estimate.GetProperty("result").GetProperty("declarants")[at].GetProperty("method").GetString());
        Assert.Equal("gross", Assert.Single(estimate.GetProperty("salaryDetail").EnumerateArray()).GetProperty("category").GetString());

        var inputs = estimate.GetProperty("inputs").Clone();
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(inputs.GetRawText())!;
        var declarant = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body["declarants"][at].GetRawText())!;
        declarant["salaryGross"] = JsonSerializer.SerializeToElement("0.00");
        declarant["salaryTaxable"] = JsonSerializer.SerializeToElement("50000.00");
        body["declarants"] = JsonSerializer.SerializeToElement(new[] { declarant });
        var simulated = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/tax/simulate", body);
        var row = JsonDocument.Parse(await simulated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("declarants")[0];
        Assert.Equal("entered", row.GetProperty("method").GetString());
        Assert.Equal("45000.00", row.GetProperty("taxable").GetString());

        declarant["salaryTaxable"] = JsonSerializer.SerializeToElement("-1");
        body["declarants"] = JsonSerializer.SerializeToElement(new[] { declarant });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/tax/simulate", body)).StatusCode);
    }

    [Fact]
    public async Task The_dashboard_counts_a_twelfth_of_this_years_tax_as_a_commitment_taken_out_of_the_rest()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var salary = await app.NewIncomeSourceAsync("Salaire A", app.AdultA);
        await AddPhaseAsync(app, app.AdultA, salary, "78000", "100000");

        var total = decimal.Parse(JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/tax?scope=me")).RootElement
            .GetProperty("result").GetProperty("totalTax").GetString()!, CultureInfo.InvariantCulture);
        var dashboard = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/dashboard?scope=me")).RootElement;

        Assert.True(total > 0m);
        var tax = Assert.Single(dashboard.GetProperty("commitmentSources").EnumerateArray(), s => s.GetProperty("source").GetString() == "tax");
        Assert.Equal(Math.Round(total / 12, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture),
            tax.GetProperty("amortized").GetProperty("amount").GetString());
        Assert.Equal(Math.Round(6500m - total / 12, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture), // 78000 / 12 − the tax
            dashboard.GetProperty("margin").GetProperty("amount").GetString());
    }

    [Fact]
    public async Task Every_PER_payment_of_the_year_is_deducted_the_opening_of_a_PER_opened_that_year_included()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var old = await PerAsync(app, app.AdultA, "PER ancien", "2020-03-01", "2025-01-10"); // tracked from 2025: its opening is older payments
        await PerAsync(app, app.AdultA, "PER neuf", "2025-02-01", "2025-02-01");
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{old}/flows",
            new { occurredOn = "2025-06-15", kind = "contribution", amount = new { amount = "500.00", currency = "EUR" } })).EnsureSuccessStatusCode();

        Assert.Equal("1500.00", PerOf(await EstimateAsync(app, app.AdultA, 2025))[app.AdultA.Value]);
    }

    [Fact]
    public async Task Each_adults_shared_PER_goes_to_its_owner_in_both_scopes_and_a_private_one_only_to_its_owner()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PerAsync(app, app.AdultA, "PER A", "2025-02-01", "2025-02-01");
        await PerAsync(app, app.AdultB, "PER B", "2025-03-01", "2025-03-01");
        await PerAsync(app, app.AdultB, "PER B privé", "2025-04-01", "2025-04-01", visibility: "private");

        foreach (var scope in new[] { "household", "me" })
        {
            var seenByA = PerOf(await EstimateAsync(app, app.AdultA, 2025, scope));
            Assert.Equal("1000.00", seenByA[app.AdultA.Value]);
            Assert.Equal("1000.00", seenByA[app.AdultB.Value]);
        }
        Assert.Equal("2000.00", PerOf(await EstimateAsync(app, app.AdultB, 2025))[app.AdultB.Value]);
    }

    [Fact]
    public async Task A_saved_profile_gives_its_credits_and_stands_in_for_the_next_year()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await app.NewIncomeSourceAsync("Salaire", app.AdultA);
        await AddPhaseAsync(app, app.AdultA, source, "39000", "50000");

        var saved = await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/tax/profile/2025?scope=me", new
        {
            dependents = 2, childrenUnder6 = 2, childcareOutsideHome = "6000", pinel = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var result = JsonDocument.Parse(await saved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("result");
        Assert.Equal("3000.00", result.GetProperty("childcareCredit").GetString());
        Assert.Equal("3.0", result.GetProperty("parts").GetString());

        var next = await EstimateAsync(app, app.AdultB, 2026, "household");
        Assert.Equal(2025, next.GetProperty("profileFrom").GetInt32());
        Assert.Equal("6000.00", next.GetProperty("profile").GetProperty("childcareOutsideHome").GetString());
    }

    /// <summary>Issue #12: an estimate has data from a tax line or a profile stored for its year; an earlier profile standing in is not enough.</summary>
    [Fact]
    public async Task An_estimate_has_data_from_a_line_or_its_years_profile()
    {
        static async Task<bool> HasDataAsync(NidApp app, int year)
        {
            await using var scope = app.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
            var read = await scope.ServiceProvider.GetRequiredService<TaxEstimates>()
                .ComputeWithDataAsync(year, VisibilityScope.Me, NidApp.GoldenCaseToday, TestContext.Current.CancellationToken);
            Assert.True(read.TryGetValue(out var result));
            return result!.HasData;
        }

        await using var profiled = await NidApp.StartAsync(fixture);
        Assert.False(await HasDataAsync(profiled, 2025));
        Assert.Equal(HttpStatusCode.OK, (await profiled.RawAsync(profiled.AdultA, HttpMethod.Put, "/api/v1/tax/profile/2025?scope=me",
            new { dependents = 1, pinel = Array.Empty<object>() })).StatusCode);
        Assert.Equal((true, false), (await HasDataAsync(profiled, 2025), await HasDataAsync(profiled, 2026)));

        await using var earning = await NidApp.StartAsync(fixture);
        var source = await earning.NewIncomeSourceAsync("Salaire", earning.AdultA);
        await AddPhaseAsync(earning, earning.AdultA, source, "39000", "50000");
        Assert.True(await HasDataAsync(earning, 2025));
    }

    [Fact]
    public async Task A_profile_out_of_bounds_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/tax/profile/2025", new
        {
            childrenUnder6 = 0, pinel = new[] { new { property = Guid.NewGuid(), initialYears = 12, totalYears = 12, startYear = (int?)null, plus = false } },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/tax?year=2024", null)).StatusCode);
    }

    [Fact]
    public async Task A_simulation_computes_given_inputs_and_checks_them()
    {
        await using var app = await NidApp.StartAsync(fixture);
        object Body(string status) => new
        {
            year = 2025, dependents = 2, childrenUnder6 = 2, childcareOutsideHome = "6000",
            declarants = new[]
            {
                new { name = "A", salaryGross = "50000", status = "non_executive", otherTaxable = "0", perContributions = "0" },
                new { name = "B", salaryGross = "40000", status, otherTaxable = "0", perContributions = "0" },
            },
        };

        var computed = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/tax/simulate", Body("executive"));
        Assert.Equal(HttpStatusCode.OK, computed.StatusCode);
        var result = JsonDocument.Parse(await computed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("247.00", result.GetProperty("incomeTax").GetString()); // TaxCalculatorTests' first example
        Assert.Equal("64407.00", result.GetProperty("netTaxableIncome").GetString());
        Assert.Equal("4.5", result.GetProperty("withholding").GetProperty("rate").GetString());

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/tax/simulate", Body("boss"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("declarants[1].status", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_profiles_exempt_income_goes_into_the_reference_income_only()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await app.NewIncomeSourceAsync("Salaire", app.AdultA);
        await AddPhaseAsync(app, app.AdultA, source, "39000", "50000");
        var adult = app.AdultA.Value.ToString();

        var estimate = await PutProfileAsync(app, app.AdultA, 2025, new
        {
            pinel = Array.Empty<object>(), exemptIncomes = new Dictionary<string, string> { [adult] = "2500.40" },
        });

        var result = estimate.GetProperty("result");
        var taxable = DecimalOf(result, "netTaxableIncome");
        // The exempt overtime counts after the 10 % allowance: 2 500.40 − 250.04.
        Assert.Equal(Math.Floor(taxable + 2_250.36m).ToString("0.00", CultureInfo.InvariantCulture), result.GetProperty("referenceIncome").GetString());
        Assert.Equal("2500.40", estimate.GetProperty("profile").GetProperty("exemptIncomes").GetProperty(adult).GetString());
        Assert.Equal("2500.40", estimate.GetProperty("inputs").GetProperty("declarants")[IndexOf(estimate, app.AdultA)].GetProperty("exemptIncome").GetString());

        var simulated = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/tax/simulate", new
        {
            year = 2025,
            declarants = new[] { new { name = "A", salaryGross = "0", status = "non_executive", otherTaxable = "50000", perContributions = "0", exemptIncome = "1000" } },
        });
        Assert.Equal("45900.00", JsonDocument.Parse(await simulated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("referenceIncome").GetString());

        var negative = await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/tax/profile/2025", new
        {
            pinel = Array.Empty<object>(), exemptIncomes = new Dictionary<string, string> { [adult] = "-1" },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, negative.StatusCode);
    }

    private static async Task ContributeAsync(NidApp app, PersonId owner, string envelope, string on, string amount)
        => (await app.RawAsync(owner, HttpMethod.Post, $"/api/v1/investments/envelopes/{envelope}/flows",
            new { occurredOn = on, kind = "contribution", amount = new { amount, currency = "EUR" } })).EnsureSuccessStatusCode();

    private static async Task ValueAsync(NidApp app, PersonId owner, string envelope, string asOf, string amount)
        => (await app.RawAsync(owner, HttpMethod.Post, $"/api/v1/investments/envelopes/{envelope}/valuations",
            new { asOf, amount = new { amount, currency = "EUR" } })).EnsureSuccessStatusCode();

    private static async Task<JsonElement> PutProfileAsync(NidApp app, PersonId reader, int year, object body)
    {
        var response = await app.RawAsync(reader, HttpMethod.Put, $"/api/v1/tax/profile/{year}?scope=me", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static JsonElement PerResultOf(JsonElement estimate, PersonId adult)
        => estimate.GetProperty("result").GetProperty("declarants")[IndexOf(estimate, adult)].GetProperty("per");

    private static decimal DecimalOf(JsonElement element, string name) => decimal.Parse(element.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_notice_ceiling_entered_in_the_profile_drives_the_deduction()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var per = await PerAsync(app, app.AdultA, "PER ancien", "2020-03-01", "2024-01-10");
        await ContributeAsync(app, app.AdultA, per, "2025-03-01", "8000.00");

        var saved = await PutProfileAsync(app, app.AdultA, 2025, new
        {
            pinel = Array.Empty<object>(), perCeilings = new Dictionary<string, string> { [app.AdultA.Value.ToString()] = "12000" },
        });

        var result = PerResultOf(saved, app.AdultA);
        Assert.Equal("8000.00", result.GetProperty("deducted").GetString());
        Assert.Equal("4000.00", result.GetProperty("remaining").GetString());
        Assert.Equal("12000.00", result.GetProperty("ceiling").GetString());
        Assert.Equal("notice", result.GetProperty("source").GetString());
        Assert.Equal("12000.00", saved.GetProperty("profile").GetProperty("perCeilings").GetProperty(app.AdultA.Value.ToString()).GetString());
        var declarant = saved.GetProperty("inputs").GetProperty("declarants")[IndexOf(saved, app.AdultA)];
        Assert.Equal("12000.00", declarant.GetProperty("perCeiling").GetString());
        Assert.Equal(["PER ancien"], declarant.GetProperty("perEnvelopes").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain(saved.GetProperty("result").GetProperty("warnings").EnumerateArray(), w => w.GetString() == "per_ceiling_estimated");
    }

    [Fact]
    public async Task Without_a_notice_the_ceiling_is_estimated_on_last_years_salaries()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var salary = await app.NewIncomeSourceAsync("Salaire A", app.AdultA);
        await AddPaymentAsync(app, app.AdultA, salary, "Salaire 2024", "2024-06-30", "78000", "100000", null); // last year's only income
        var per = await PerAsync(app, app.AdultA, "PER ancien", "2020-03-01", "2024-01-10");
        await ContributeAsync(app, app.AdultA, per, "2025-03-01", "3000.00");

        // What 2024's 100 000 gross comes to once taxable (the rules of 2025 stand in for 2024): the simulation, no PER.
        var simulated = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/tax/simulate", new
        {
            year = 2025, declarants = new[] { new { name = "A", salaryGross = "100000", status = "non_executive", otherTaxable = "0", perContributions = "0" } },
        });
        var taxable2024 = DecimalOf(JsonDocument.Parse(await simulated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("declarants")[0], "taxable");
        var estimate = await EstimateAsync(app, app.AdultA, 2025);

        var result = PerResultOf(estimate, app.AdultA);
        var ceiling = DecimalOf(result, "ceiling");
        Assert.Equal("estimated", result.GetProperty("source").GetString());
        Assert.True(ceiling > 4710m); // above the 10 % PASS floor
        Assert.InRange(ceiling, taxable2024 / 10 - 1m, taxable2024 / 10 + 1m);
        Assert.Contains(estimate.GetProperty("result").GetProperty("warnings").EnumerateArray(), w => w.GetString() == "per_ceiling_estimated");
        Assert.Equal(taxable2024, DecimalOf(estimate.GetProperty("inputs").GetProperty("declarants")[IndexOf(estimate, app.AdultA)], "previousYearIncome"));
    }

    [Fact]
    public async Task The_other_adults_private_salary_of_last_year_does_not_change_the_estimated_ceilings()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var salary = await app.NewIncomeSourceAsync("Salaire A", app.AdultA);
        await AddPaymentAsync(app, app.AdultA, salary, "Salaire 2024", "2024-06-30", "39000", "50000", null);
        await PerAsync(app, app.AdultA, "PER A", "2020-03-01", "2024-01-10");
        await PerAsync(app, app.AdultB, "PER B", "2020-03-01", "2024-01-10");
        async Task<(string, string, string, string)> CeilingsAsync()
        {
            var me = await EstimateAsync(app, app.AdultA, 2025);
            var household = await EstimateAsync(app, app.AdultA, 2025, "household");
            return (DecimalOf(PerResultOf(me, app.AdultA), "ceiling").ToString(CultureInfo.InvariantCulture),
                DecimalOf(PerResultOf(household, app.AdultA), "ceiling").ToString(CultureInfo.InvariantCulture),
                DecimalOf(PerResultOf(household, app.AdultB), "ceiling").ToString(CultureInfo.InvariantCulture),
                household.GetProperty("inputs").GetProperty("declarants")[IndexOf(household, app.AdultB)].GetProperty("previousYearIncome").GetString()!);
        }
        var before = await CeilingsAsync();

        var theirs = await app.NewIncomeSourceAsync("Salaire B", app.AdultB, visibility: "private");
        await AddPaymentAsync(app, app.AdultB, theirs, "Salaire 2024", "2024-06-30", "78000", "100000", null);

        Assert.Equal(before, await CeilingsAsync());
    }

    [Fact]
    public async Task Estimated_ceiling_of_the_first_rules_year_uses_the_previous_years_pass()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PerAsync(app, app.AdultA, "PER neuf", "2025-02-01", "2025-02-01");

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/tax?year=2025&scope=me", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var estimate = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        // No 2024 income: the floor, 10 % of the 2024 PASS (4 637 €, brochure IR 2026).
        Assert.Equal(4637m, DecimalOf(PerResultOf(estimate, app.AdultA), "ceiling"));
    }

    [Fact]
    public async Task A_PER_without_contributions_this_year_is_flagged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var idle = await PerAsync(app, app.AdultA, "PER dormant", "2020-03-01", "2024-01-10");
        await ValueAsync(app, app.AdultA, idle, "2025-03-01", "5000.00");
        var active = await PerAsync(app, app.AdultA, "PER actif", "2020-03-01", "2024-01-10");
        await ValueAsync(app, app.AdultA, active, "2025-03-01", "5000.00");
        await ContributeAsync(app, app.AdultA, active, "2025-03-01", "200.00");
        await PerAsync(app, app.AdultA, "PER sans valeur", "2020-03-01", "2024-01-10"); // no valuation: its value is unknown

        var estimate = await EstimateAsync(app, app.AdultA, 2025);

        var notice = Assert.Single(estimate.GetProperty("notices").EnumerateArray(), n => n.GetProperty("code").GetString() == "per_without_contributions");
        Assert.Equal("PER dormant", notice.GetProperty("name").GetString());
        Assert.Equal("200.00", PerResultOf(estimate, app.AdultA).GetProperty("contributions").GetString());
    }

    [Fact]
    public async Task A_PER_flagged_alone_gives_no_contributions()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var idle = await PerAsync(app, app.AdultA, "PER dormant", "2020-03-01", "2024-01-10");
        await ValueAsync(app, app.AdultA, idle, "2025-03-01", "5000.00");

        var estimate = await EstimateAsync(app, app.AdultA, 2025);

        Assert.Single(estimate.GetProperty("notices").EnumerateArray(), n => n.GetProperty("code").GetString() == "per_without_contributions");
        Assert.Equal("0.00", PerResultOf(estimate, app.AdultA).GetProperty("contributions").GetString());
    }

    [Fact]
    public async Task An_invalid_PER_profile_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var adult = app.AdultA.Value.ToString();

        var negative = await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/tax/profile/2025", new
        {
            pinel = Array.Empty<object>(), perCeilings = new Dictionary<string, string> { [adult] = "-1" },
        });
        var above = await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/tax/profile/2025", new
        {
            pinel = Array.Empty<object>(), perCeilings = new Dictionary<string, string> { [adult] = "5000" },
            perCeilingsExpiring = new Dictionary<string, string> { [adult] = "6000" },
        });
        var orphan = await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/tax/profile/2025", new
        {
            pinel = Array.Empty<object>(), perCeilingsExpiring = new Dictionary<string, string> { [adult] = "100" },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, negative.StatusCode);
        Assert.Contains("perCeilings", await negative.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, above.StatusCode);
        Assert.Contains("tax.invalid_profile", await above.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, orphan.StatusCode);
        Assert.Contains("tax.invalid_profile", await orphan.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pooling_is_saved_and_applied()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var per = await PerAsync(app, app.AdultA, "PER A", "2020-03-01", "2024-01-10");
        await ContributeAsync(app, app.AdultA, per, "2025-03-01", "7000.00");
        object Body(bool pooling) => new
        {
            pinel = Array.Empty<object>(), perPooling = pooling,
            perCeilings = new Dictionary<string, string> { [app.AdultA.Value.ToString()] = "5000", [app.AdultB.Value.ToString()] = "8000" },
        };

        var alone = await PutProfileAsync(app, app.AdultA, 2025, Body(false));
        Assert.Equal("2000.00", PerResultOf(alone, app.AdultA).GetProperty("excess").GetString());
        Assert.False(alone.GetProperty("result").GetProperty("per").GetProperty("pooled").GetBoolean());
        Assert.Contains(alone.GetProperty("result").GetProperty("warnings").EnumerateArray(), w => w.GetString() == "per_over_ceiling");

        var pooled = await PutProfileAsync(app, app.AdultA, 2025, Body(true));
        Assert.True(pooled.GetProperty("profile").GetProperty("perPooling").GetBoolean());
        Assert.True(pooled.GetProperty("inputs").GetProperty("perPooling").GetBoolean());
        var household = pooled.GetProperty("result").GetProperty("per");
        Assert.True(household.GetProperty("pooled").GetBoolean());
        Assert.Equal("6000.00", household.GetProperty("remaining").GetString());
        Assert.Equal("0.00", PerResultOf(pooled, app.AdultA).GetProperty("excess").GetString());
        Assert.Equal("7000.00", PerResultOf(pooled, app.AdultA).GetProperty("deducted").GetString());
        Assert.DoesNotContain(pooled.GetProperty("result").GetProperty("warnings").EnumerateArray(), w => w.GetString() == "per_over_ceiling");
    }

    [Fact]
    public async Task A_notice_ceiling_counts_for_its_own_year_only()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PutProfileAsync(app, app.AdultA, 2025, new
        {
            pinel = Array.Empty<object>(), childcareOutsideHome = "500", perPooling = true,
            perCeilings = new Dictionary<string, string> { [app.AdultA.Value.ToString()] = "12000" },
        });

        var next = await EstimateAsync(app, app.AdultA, 2026);

        Assert.Equal(2025, next.GetProperty("profileFrom").GetInt32());
        Assert.Equal("estimated", PerResultOf(next, app.AdultA).GetProperty("source").GetString());
        Assert.Empty(next.GetProperty("profile").GetProperty("perCeilings").EnumerateObject());
        Assert.Equal("500.00", next.GetProperty("profile").GetProperty("childcareOutsideHome").GetString()); // the rest carries forward
        Assert.True(next.GetProperty("profile").GetProperty("perPooling").GetBoolean());
    }

    [Fact]
    public async Task A_PER_owned_by_a_dependent_gives_no_notice_without_contributions()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var child = JsonDocument.Parse(await (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = "PER enfant", kind = "per", currency = "EUR", visibility = "shared", openedOn = "2020-03-01",
            owners = new[] { new { personId = app.Dependent.Value, shareBp = 10_000 } },
            opening = new { occurredOn = "2024-01-10", amount = new { amount = "1000.00", currency = "EUR" } },
        })).EnsureSuccessStatusCode().Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("id").GetString()!;
        await ValueAsync(app, app.AdultA, child, "2025-03-01", "5000.00");

        var estimate = await EstimateAsync(app, app.AdultA, 2025, "household");

        Assert.DoesNotContain(estimate.GetProperty("notices").EnumerateArray(), n => n.GetProperty("code").GetString() == "per_without_contributions");
    }
}
