using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.Core.IntegrationTests.Dashboard;

/// <summary>3E1 spec §2: properties and loans in the net worth, its split, its change since 1 January and its curve.</summary>
public class RealEstateNetWorthTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<Guid> EstimateAsync(NidApp app, Guid property, string asOf, string amount)
        => await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/valuations", new { asOf, amount }));

    /// <summary><see cref="Short"/> (12 000 at 0 %, twelve payments of 1 000), disbursed and first paid on the given dates.</summary>
    private static Dictionary<string, object?> Loan(NidApp app, string name, string disbursedOn, string firstPaymentDate)
    {
        var body = Short(app, name);
        body["disbursedOn"] = disbursedOn;
        body["firstPaymentDate"] = firstPaymentDate;
        return body;
    }

    private static async Task<JsonElement> DashboardAsync(NidApp app, PersonId reader, string scope = "household")
        => JsonDocument.Parse(await app.GetRawAsync(reader, $"/api/v1/dashboard?scope={scope}")).RootElement;

    private static string? Total(JsonElement figure)
        => figure.GetProperty("total").ValueKind == JsonValueKind.Null ? null : figure.GetProperty("total").GetProperty("amount").GetString();

    private static string? Amount(JsonElement money) => money.ValueKind == JsonValueKind.Null ? null : money.GetProperty("amount").GetString();

    [Fact]
    public async Task A_property_and_its_loan_are_two_lines_the_loan_negative_each_split_by_its_own_shares()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app, "Maison", "shared", app.AdultA, app.AdultB)); // 50 / 50
        await EstimateAsync(app, home, "2024-12-31", "300000");
        await EstimateAsync(app, home, "2025-09-30", "310000");
        var land = await NewPropertyAsync(app, Home(app, "Terrain")); // never estimated
        await NewAsync(app, Short(app, "Prêt travaux")); // A alone; ten payments of 1 000 made by 2025-10-15: 2 000 owed

        var view = await DashboardAsync(app, app.AdultA);

        Assert.Equal("308000.00", Total(view.GetProperty("netWorth")));
        Assert.Equal(3, view.GetProperty("envelopes").GetInt32());
        Assert.Equal(1, view.GetProperty("envelopesWithoutValue").GetInt32());
        Assert.Equal(0, view.GetProperty("staleEnvelopes").GetInt32());

        var rows = view.GetProperty("envelopeSplit").EnumerateArray().ToList();
        Assert.Equal(new[] { "Maison", "Prêt travaux", "Terrain" }, rows.Select(r => r.GetProperty("name").GetString()));
        Assert.Equal(new[] { "properties", "loans", "properties" }, rows.Select(r => r.GetProperty("module").GetString()));
        Assert.Equal("310000.00", Amount(rows[0].GetProperty("converted")));
        Assert.Equal("2025-09-30", rows[0].GetProperty("asOf").GetString());
        Assert.Equal("-2000.00", Amount(rows[1].GetProperty("value")));
        Assert.Equal("-2000.00", Amount(rows[1].GetProperty("converted")));
        Assert.Equal("2025-10-15", rows[1].GetProperty("asOf").GetString());
        Assert.False(rows[1].GetProperty("stale").GetBoolean());
        Assert.Null(Amount(rows[2].GetProperty("value")));

        // README §8: no netting — A holds half the property and the whole loan, B half the property.
        var people = view.GetProperty("people").EnumerateArray().ToDictionary(p => p.GetProperty("id").GetGuid());
        Assert.Equal("153000.00", Amount(people[app.AdultA.Value].GetProperty("amount")));
        Assert.Equal("155000.00", Amount(people[app.AdultB.Value].GetProperty("amount")));
        Assert.Equal(49.7m, people[app.AdultA.Value].GetProperty("pct").GetDecimal());

        // The estimate's 10 000 is a gain; the loan's 10 000 of principal repaid since 1 January is paid in, its gain 0.
        Assert.Equal("10000.00", Total(view.GetProperty("change")));
        Assert.Equal("10000.00", Total(view.GetProperty("changeContributions")));
        Assert.Equal(3.4m, view.GetProperty("changePct").GetDecimal()); // 10 000 ÷ (300 000 − 12 000 + 10 000)
        Assert.Equal(0, view.GetProperty("envelopesWithoutStart").GetInt32());

        var todo = Assert.Single(view.GetProperty("todo").EnumerateArray());
        Assert.Equal(("no_value", "properties", land), (todo.GetProperty("kind").GetString(), todo.GetProperty("module").GetString(), todo.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task A_private_loan_on_a_shared_property_is_deducted_only_in_Moi()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app));
        await EstimateAsync(app, home, "2025-09-30", "310000");
        var body = Short(app, "Prêt perso");
        body["visibility"] = "private";
        body["propertyId"] = home;
        var loan = await NewAsync(app, body);

        var household = await DashboardAsync(app, app.AdultA, "household");
        var me = await DashboardAsync(app, app.AdultA, "me");
        var bob = await DashboardAsync(app, app.AdultB, "me");

        Assert.Equal("310000.00", Total(household.GetProperty("netWorth"))); // README §8 visibility_mismatch: not deducted
        Assert.DoesNotContain(household.GetProperty("envelopeSplit").EnumerateArray(), r => r.GetProperty("id").GetGuid() == loan);
        Assert.Equal("308000.00", Total(me.GetProperty("netWorth")));
        Assert.Contains(me.GetProperty("envelopeSplit").EnumerateArray(), r => r.GetProperty("id").GetGuid() == loan);
        Assert.Equal("310000.00", Total(bob.GetProperty("netWorth")));
        Assert.DoesNotContain(bob.GetProperty("envelopeSplit").EnumerateArray(), r => r.GetProperty("id").GetGuid() == loan);
    }

    [Fact]
    public async Task A_stale_estimate_and_a_loan_of_this_year_stay_out_of_the_change_and_an_ended_loan_out_of_the_split()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var old = await NewPropertyAsync(app, Home(app, "Ancienne"));
        await EstimateAsync(app, old, "2023-06-01", "250000"); // yearly: more than 400 days old, today and on 1 January alike
        await NewAsync(app, Loan(app, "Prêt récent", "2025-02-01", "2025-02-15")); // nine payments made: 3 000 owed; nothing on 1 January
        var ended = await NewAsync(app, Loan(app, "Prêt soldé", "2024-01-01", "2024-01-15")); // last payment 2024-12-15

        var view = await DashboardAsync(app, app.AdultA);

        Assert.Equal("247000.00", Total(view.GetProperty("netWorth")));
        Assert.Equal(2, view.GetProperty("envelopes").GetInt32());
        Assert.Equal(1, view.GetProperty("staleEnvelopes").GetInt32());
        Assert.Equal(2, view.GetProperty("envelopesWithoutStart").GetInt32());
        Assert.Null(Total(view.GetProperty("change")));
        Assert.DoesNotContain(view.GetProperty("envelopeSplit").EnumerateArray(), r => r.GetProperty("id").GetGuid() == ended);
        var todo = Assert.Single(view.GetProperty("todo").EnumerateArray());
        Assert.Equal(("stale_value", old, "2023-06-01"), (todo.GetProperty("kind").GetString(), todo.GetProperty("id").GetGuid(), todo.GetProperty("on").GetString()));
    }

    [Fact]
    public async Task The_curve_counts_a_property_at_its_price_until_its_first_estimate_and_a_loan_from_its_disbursement_its_principal_paid_in()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app)); // bought 2020-06-15 for 250 000
        await EstimateAsync(app, home, "2025-03-31", "300000");
        await NewPropertyAsync(app, Home(app, "Terrain")); // never estimated: out of the curve, as out of today's net worth
        var later = Home(app, "Studio");
        later["purchaseDate"] = "2025-02-10";
        later["purchasePrice"] = "90000";
        await EstimateAsync(app, await NewPropertyAsync(app, later), "2025-09-30", "95000");
        await NewAsync(app, Short(app, "Prêt travaux")); // disbursed 2025-01-01, 1 000 repaid on each 15th

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/dashboard/net-worth?scope=household");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var points = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("points").EnumerateArray()
            .ToDictionary(p => p.GetProperty("on").GetString()!);

        // Before the first estimate, the price paid; the studio only from its purchase.
        Assert.Equal(("250000.00", "0.00"), (Amount(points["2024-12-31"].GetProperty("value")), Amount(points["2024-12-31"].GetProperty("contributions"))));
        Assert.Equal(("239000.00", "1000.00"), (Amount(points["2025-01-31"].GetProperty("value")), Amount(points["2025-01-31"].GetProperty("contributions"))));
        Assert.Equal("330000.00", Amount(points["2025-02-28"].GetProperty("value"))); // 250 000 + 90 000 − 10 000 owed
        Assert.Equal(("381000.00", "3000.00"), (Amount(points["2025-03-31"].GetProperty("value")), Amount(points["2025-03-31"].GetProperty("contributions"))));
        Assert.Equal(("393000.00", "10000.00"), (Amount(points["2025-10-15"].GetProperty("value")), Amount(points["2025-10-15"].GetProperty("contributions"))));
    }

    [Fact]
    public async Task A_loan_without_a_property_draws_its_balance_alone_from_its_disbursement()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewAsync(app, Short(app, "Prêt travaux")); // disbursed 2025-01-01, 1 000 repaid on each 15th

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/dashboard/net-worth?scope=household");
        var points = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("points").EnumerateArray()
            .ToDictionary(p => p.GetProperty("on").GetString()!);

        Assert.Null(Amount(points["2024-12-31"].GetProperty("value")));
        Assert.Equal(("-11000.00", "1000.00"), (Amount(points["2025-01-31"].GetProperty("value")), Amount(points["2025-01-31"].GetProperty("contributions"))));
        Assert.Equal(("-9000.00", "3000.00"), (Amount(points["2025-03-31"].GetProperty("value")), Amount(points["2025-03-31"].GetProperty("contributions"))));
        Assert.Equal(("-2000.00", "10000.00"), (Amount(points["2025-10-15"].GetProperty("value")), Amount(points["2025-10-15"].GetProperty("contributions"))));
    }
}
