using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.Core.IntegrationTests.TaxEstimate;

/// <summary>
/// Rental 2044 spec §3 to §5, through the real API. The host's today is 2025-10-15: 2025 is the current year (provisional, the
/// tenancies' rents projected from October on), 2026 the next one.
/// </summary>
public class Rental2044Tests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<JsonElement> FormAsync(NidApp app, PersonId reader, int year, string scope = "household")
    {
        var response = await app.RawAsync(reader, HttpMethod.Get, $"/api/v1/tax/2044?year={year}&scope={scope}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static async Task<JsonElement> EstimateAsync(NidApp app, PersonId reader, int year, string scope = "household")
        => JsonDocument.Parse(await app.GetRawAsync(reader, $"/api/v1/tax?year={year}&scope={scope}")).RootElement;

    private static async Task RegimeAsync(NidApp app, string regime)
        => Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/tax/profile/2025",
            new { childrenUnder6 = 0, rentalRegime = regime, pinel = Array.Empty<object>() })).StatusCode);

    private static async Task PostAsync(NidApp app, ItemId property, string route, object body, PersonId? actor = null)
    {
        var response = await app.RawAsync(actor ?? app.AdultA, HttpMethod.Post, $"{Properties}/{property.Value}/{route}", body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private static Task FlowAsync(NidApp app, ItemId property, string on, string kind, string category, string amount, string? note = null, PersonId? actor = null)
        => PostAsync(app, property, "flows", new { occurredOn = on, kind, category, amount, note }, actor);

    private static Task<ItemId> RentalAsync(NidApp app, string name, PersonId? owner = null, Visibility visibility = Visibility.Shared)
        => app.NewPropertyAsync(name, visibility, (owner ?? app.AdultA, 10_000));

    private static JsonElement PropertyOf(JsonElement form, string name)
        => Assert.Single(form.GetProperty("properties").EnumerateArray(), p => S(p, "name") == name);

    private static string? Line(JsonElement property, string line)
        => S(Assert.Single(property.GetProperty("lines").EnumerateArray(), l => S(l, "line") == line), "amount");

    private static string? Total(JsonElement form, string name) => S(form.GetProperty("totals"), name);

    private static List<(string? Code, Guid Item, string? Amount)> Notices(JsonElement form)
        => form.GetProperty("notices").EnumerateArray().Select(n => (S(n, "code"), n.GetProperty("item").GetGuid(), S(n, "amount"))).ToList();

    private static decimal D(string? amount) => decimal.Parse(amount!, CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_form_lists_each_property_by_line_and_the_households_result()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RegimeAsync(app, "real");
        var a = await RentalAsync(app, "Appartement A");
        await FlowAsync(app, a, "2025-03-01", "income", "rent", "9600");
        await FlowAsync(app, a, "2025-04-01", "expense", "insurance", "300");
        await FlowAsync(app, a, "2025-05-01", "expense", "property_tax", "900.40");
        await FlowAsync(app, a, "2025-06-01", "expense", "condo_fees", "600");
        await FlowAsync(app, a, "2025-07-01", "expense", "management_fees", "480");
        await FlowAsync(app, a, "2025-07-02", "expense", "energy", "250"); // never deductible
        var b = await RentalAsync(app, "Appartement B");
        await FlowAsync(app, b, "2025-03-01", "income", "rent", "6000");
        await FlowAsync(app, b, "2025-04-01", "expense", "maintenance", "1200");
        await FlowAsync(app, b, "2025-05-10", "expense", "works", "3000", "Toiture");
        await FlowAsync(app, b, "2025-06-10", "expense", "other", "150");

        var form = await FormAsync(app, app.AdultA, 2025);

        Assert.Equal((2025, "real", true), (form.GetProperty("year").GetInt32(), S(form, "regime"), form.GetProperty("provisional").GetBoolean()));
        Assert.Equal(["Appartement A", "Appartement B"], form.GetProperty("properties").EnumerateArray().Select(p => S(p, "name")));
        var first = PropertyOf(form, "Appartement A");
        Assert.Equal(a.Value, first.GetProperty("id").GetGuid());
        Assert.Equal(["211", "215", "221", "222", "223", "224", "227", "229", "230", "240", "250", "263"],
            first.GetProperty("lines").EnumerateArray().Select(l => S(l, "line")));
        Assert.Equal(["9600.00", "9600.00", "480.00", "20.00", "300.00", "0.00", "900.00", "600.00", "0.00", "2300.00", "0.00", "7300.00"],
            first.GetProperty("lines").EnumerateArray().Select(l => S(l, "amount")));
        var second = PropertyOf(form, "Appartement B");
        Assert.Equal(("6000.00", "1200.00", "1220.00", "4780.00"), (Line(second, "211"), Line(second, "224"), Line(second, "240"), Line(second, "263")));

        Assert.Equal([(b.Value, "2025-05-10", "works", "Toiture", "3000.00"), (b.Value, "2025-06-10", "other", null, "150.00")],
            form.GetProperty("toCheck").EnumerateArray().Select(c => (c.GetProperty("property").GetGuid(), S(c, "occurredOn"), S(c, "category"), S(c, "note"), S(c, "amount"))));
        Assert.Equal(("15600.00", "3520.00", "0.00", "12080.00"), (Total(form, "receipts"), Total(form, "charges"), Total(form, "interest"), Total(form, "result")));
        Assert.Equal(("12080.00", "0.00", "0.00", "0.00"), (Total(form, "box4BA"), Total(form, "box4BB"), Total(form, "box4BC"), Total(form, "box4BE")));
    }

    [Theory]
    [InlineData(2025)]
    [InlineData(2026)]
    public async Task The_estimate_and_the_form_give_the_same_rental_income(int year)
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RegimeAsync(app, "real");
        var home = await RentalAsync(app, "Locatif");
        await PostAsync(app, home, "tenancies", new { label = "T2", startsOn = "2021-01-01", rentMonthly = "700.40", chargesMonthly = "50" });
        foreach (var month in Enumerable.Range(1, 9))
            await FlowAsync(app, home, $"2025-{month:00}-05", "income", "rent", "700.40");
        await FlowAsync(app, home, "2025-04-01", "expense", "maintenance", "9000.35");
        var loan = Golden(app, "Prêt locatif");
        loan["propertyId"] = home.Value;
        await NewAsync(app, loan);

        var estimate = (await EstimateAsync(app, app.AdultA, year)).GetProperty("result");
        var form = await FormAsync(app, app.AdultA, year);

        Assert.Equal(S(estimate, "rentalTaxable"), Total(form, "box4BA"));
        Assert.Equal(S(estimate, "rentalDeficit"), Total(form, "box4BC"));
        // 2025: the maintenance makes a deficit on the global income; 2026: the rents exceed the interest and the 20 €.
        Assert.True(D(Total(form, year == 2025 ? "box4BC" : "box4BA")) > 0);
    }

    [Fact]
    public async Task Interest_and_loan_fees_go_on_line_250()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RegimeAsync(app, "real");
        var home = await RentalAsync(app, "Locatif");
        await FlowAsync(app, home, "2025-03-01", "income", "rent", "12000");
        var body = Golden(app, "Prêt locatif");
        body["propertyId"] = home.Value;
        var loan = await NewAsync(app, body);
        foreach (var (kind, amount, onDate) in new[] { ("file", "500", "2025-02-01"), ("guarantee", "200", "2025-02-01"), ("file", "100", "2024-12-31"), ("early_repayment_penalty", "300", "2025-06-01") })
            Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Loans}/{loan}/fees", new { kind, amount, onDate })).StatusCode);
        var usd = Golden(app, "Prêt dollars");
        usd["currency"] = "USD";
        usd["propertyId"] = home.Value;
        var other = await NewAsync(app, usd);

        var schedule = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Loans}/{loan}/schedule"));
        var year = Assert.Single(schedule.GetProperty("years").EnumerateArray(), y => y.GetProperty("year").GetInt32() == 2025);
        var expected = Math.Round(D(S(year, "interest")) + D(S(year, "insurance")) + 700m, 0, MidpointRounding.AwayFromZero);

        var form = await FormAsync(app, app.AdultA, 2025);

        Assert.Equal(expected.ToString("0.00", CultureInfo.InvariantCulture), Line(PropertyOf(form, "Locatif"), "250"));
        Assert.Contains(("rental_fee_not_counted", loan, "300.00"), Notices(form));
        Assert.Contains(("other_currency", other, "0.00"), Notices(form));
    }

    [Fact]
    public async Task A_rent_recorded_with_its_charges_counts_bare() // Review Focus 1
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RegimeAsync(app, "real");
        var home = await RentalAsync(app, "Locatif");
        await PostAsync(app, home, "tenancies", new { label = "T2", startsOn = "2021-01-01", rentMonthly = "800", chargesMonthly = "50" });
        // January to October recorded before the split, rent and charges in one flow; November and December still to come.
        foreach (var month in Enumerable.Range(1, 10))
            await FlowAsync(app, home, $"2025-{month:00}-05", "income", "rent", "850");

        var form = await FormAsync(app, app.AdultA, 2025);

        Assert.Equal("9600.00", Line(PropertyOf(form, "Locatif"), "211")); // 12 × 800
        Assert.Equal([("rental_charges_removed", home.Value, "500.00")], Notices(form));
    }

    [Fact]
    public async Task Rents_recorded_per_tenancy_with_their_charges_count_bare_in_a_changeover_month() // Review Focus 1
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RegimeAsync(app, "real");
        var home = await RentalAsync(app, "Locatif");
        await PostAsync(app, home, "tenancies", new { label = "Ancien bail", startsOn = "2021-01-01", endsOn = "2025-09-09", rentMonthly = "900", chargesMonthly = "50" });
        await PostAsync(app, home, "tenancies", new { label = "Nouveau bail", startsOn = "2025-09-10", rentMonthly = "600", chargesMonthly = "50" });
        // September recorded the old way, one flow per tenancy: 9/30 of 900 + 50, then 21/30 of 600 + 50.
        await FlowAsync(app, home, "2025-09-01", "income", "rent", "285");
        await FlowAsync(app, home, "2025-09-10", "income", "rent", "455");

        var form = await FormAsync(app, app.AdultA, 2025);

        Assert.Equal("2490.00", Line(PropertyOf(form, "Locatif"), "211")); // 270 + 420, then 600 for October to December
        Assert.Equal([("rental_charges_removed", home.Value, "50.00")], Notices(form)); // 15 + 35
    }

    [Fact]
    public async Task A_bare_rent_equal_to_the_previous_tenancys_rent_and_charges_is_not_stripped()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RegimeAsync(app, "real");
        var home = await RentalAsync(app, "Locatif");
        await PostAsync(app, home, "tenancies", new { label = "A", startsOn = "2021-01-01", endsOn = "2025-09-09", rentMonthly = "650", chargesMonthly = "50" });
        await PostAsync(app, home, "tenancies", new { label = "B", startsOn = "2025-09-10", rentMonthly = "300", chargesMonthly = "0" });
        // B's bare 21/30 of 300 is 210, also A's 9/30 of 650 + 50: B's flow must not be read as A's rent and charges (195 bare).
        await FlowAsync(app, home, "2025-09-10", "income", "rent", "210");

        var form = await FormAsync(app, app.AdultA, 2025);

        Assert.Equal("1110.00", Line(PropertyOf(form, "Locatif"), "211")); // 210, then 300 for October to December
        Assert.DoesNotContain(Notices(form), n => n.Code == "rental_charges_removed");
    }

    [Fact]
    public async Task A_month_with_its_charges_recorded_apart_counts_its_rent_as_is()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RegimeAsync(app, "real");
        var home = await RentalAsync(app, "Locatif");
        await PostAsync(app, home, "tenancies", new { label = "T2", startsOn = "2021-01-01", rentMonthly = "800", chargesMonthly = "50" });
        // The rent recorded at 850 (a rent raised) beside its charges: no split to undo.
        await FlowAsync(app, home, "2025-01-05", "income", "rent", "850");
        await FlowAsync(app, home, "2025-01-05", "income", "recoverable_charges", "50");

        var form = await FormAsync(app, app.AdultA, 2025);

        Assert.Equal("3250.00", Line(PropertyOf(form, "Locatif"), "211")); // 850, then 800 for October and 2 × 800 to come
        Assert.DoesNotContain(Notices(form), n => n.Code == "rental_charges_removed");
    }

    [Fact]
    public async Task A_property_with_only_expenses_to_check_has_no_column() // Review Focus 2
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RegimeAsync(app, "real");
        var home = await RentalAsync(app, "Travaux");
        await FlowAsync(app, home, "2025-05-10", "expense", "works", "2000");
        await FlowAsync(app, home, "2025-06-10", "expense", "other", "100");

        var form = await FormAsync(app, app.AdultA, 2025);

        Assert.Empty(form.GetProperty("properties").EnumerateArray());
        Assert.Equal(["works", "other"], form.GetProperty("toCheck").EnumerateArray().Select(c => S(c, "category")));
        Assert.Equal("0.00", Total(form, "charges"));
        Assert.Equal("0.00", S((await EstimateAsync(app, app.AdultA, 2025)).GetProperty("inputs"), "rentalCharges"));
    }

    [Fact]
    public async Task The_estimate_says_what_it_leaves_out_but_the_form_does_not_repeat_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RegimeAsync(app, "real");
        var home = await RentalAsync(app, "Travaux");
        await FlowAsync(app, home, "2025-05-10", "expense", "works", "2000");
        await FlowAsync(app, home, "2025-06-10", "expense", "other", "100");

        var estimate = await EstimateAsync(app, app.AdultA, 2025);

        var notices = estimate.GetProperty("notices").EnumerateArray().Select(n => (S(n, "code"), n.GetProperty("item").GetGuid(), S(n, "amount"))).ToList();
        Assert.Equal([("rental_to_check", home.Value, "2100.00")], notices);
        Assert.DoesNotContain(Notices(await FormAsync(app, app.AdultA, 2025)), n => n.Code == "rental_to_check");
    }

    [Fact]
    public async Task A_property_in_another_currency_is_a_notice_without_a_column()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Home(app, "Dollars");
        (body["type"], body["currency"]) = ("rental", "USD");
        var home = await NewPropertyAsync(app, body);
        await FlowAsync(app, new ItemId(home), "2025-03-01", "income", "rent", "5000");

        var form = await FormAsync(app, app.AdultA, 2025);

        Assert.Empty(form.GetProperty("properties").EnumerateArray());
        Assert.Equal([("other_currency", home, "0.00")], Notices(form));
    }

    [Fact]
    public async Task A_property_without_activity_in_the_year_is_left_out() // Review Focus 2
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RegimeAsync(app, "real");
        var archived = await RentalAsync(app, "Vendu");
        await PostAsync(app, archived, "tenancies", new { label = "Fini", startsOn = "2021-01-01", endsOn = "2024-06-30", rentMonthly = "700" });
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            Assert.True((await scope.ServiceProvider.GetRequiredService<IItemRegistry>()
                .ArchiveAsync(archived, Instant.FromUtc(2024, 7, 1, 0, 0), TestContext.Current.CancellationToken)).IsSuccess);
        }
        var later = await RentalAsync(app, "Futur");
        await PostAsync(app, later, "tenancies", new { label = "Bientôt", startsOn = "2026-01-01", rentMonthly = "900" });
        var active = await RentalAsync(app, "Actif");
        await FlowAsync(app, active, "2025-03-01", "income", "rent", "1000");

        var form = await FormAsync(app, app.AdultA, 2025);

        Assert.Equal(["Actif"], form.GetProperty("properties").EnumerateArray().Select(p => S(p, "name")));
        Assert.Equal("20.00", Total(form, "charges"));
        Assert.Equal("20.00", S((await EstimateAsync(app, app.AdultA, 2025)).GetProperty("inputs"), "rentalCharges"));
        Assert.Contains("Futur", (await FormAsync(app, app.AdultA, 2026)).GetRawText());
    }

    [Fact]
    public async Task The_other_adults_private_property_stays_out_of_Foyer()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var secret = await RentalAsync(app, "Secret", app.AdultB, Visibility.Private);
        await FlowAsync(app, secret, "2025-03-01", "income", "rent", "5000", actor: app.AdultB);
        await FlowAsync(app, secret, "2025-04-01", "expense", "works", "800", actor: app.AdultB);

        var form = await FormAsync(app, app.AdultA, 2025);

        Assert.DoesNotContain("Secret", form.GetRawText());
        Assert.DoesNotContain(secret.Value.ToString(), form.GetRawText());
        Assert.Equal("0.00", Total(form, "receipts"));
        // A private item is in its owner's « Moi » only, as for the estimate.
        Assert.Equal("5000.00", Total(await FormAsync(app, app.AdultB, 2025, "me"), "receipts"));
    }

    [Fact]
    public async Task Under_micro_the_form_gives_box_4BE()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await RentalAsync(app, "Locatif");
        await FlowAsync(app, home, "2025-03-01", "income", "rent", "6000.49");
        await FlowAsync(app, home, "2025-04-01", "expense", "maintenance", "1200");

        var form = await FormAsync(app, app.AdultA, 2025);

        Assert.Equal("micro", S(form, "regime"));
        Assert.Equal(("6000.00", "0.00", "0.00", "0.00"), (Total(form, "box4BE"), Total(form, "box4BA"), Total(form, "box4BB"), Total(form, "box4BC")));
    }

    [Fact]
    public async Task A_year_out_of_the_rules_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);

        // Without a year, the form is last year's: 2024 here, before the first rules.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/tax/2044")).StatusCode);
    }
}
