using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.RealEstate.LoanApi;
using static Nid.Subscriptions.IntegrationTests.RealEstate.PropertyApi;

namespace Nid.Subscriptions.IntegrationTests.Dashboard;

/// <summary>3E1 spec §4: the loans' and properties' events of « À venir », [2025-10-15, 2025-12-14].</summary>
[Collection("postgres")]
public class RealEstateUpcomingTests(PostgresFixture fixture)
{
    private static async Task<Guid> PostAsync(NidApp app, PersonId actor, Guid property, string route, object body)
        => await CreatedAsync(await app.RawAsync(actor, HttpMethod.Post, $"{Properties}/{property}/{route}", body));

    private static async Task<List<JsonElement>> UpcomingAsync(NidApp app, PersonId reader, string scope = "household")
        => JsonDocument.Parse(await app.GetRawAsync(reader, $"/api/v1/dashboard?scope={scope}")).RootElement
            .GetProperty("upcoming").EnumerateArray().ToList();

    private static string Line(JsonElement e)
    {
        var amount = e.GetProperty("amount");
        return $"{S(e, "on")} {S(e, "kind")} {S(e, "module")} {S(e, "name")} {S(e, "label") ?? "-"} "
            + (amount.ValueKind == JsonValueKind.Null ? "-" : amount.GetProperty("amount").GetString());
    }

    [Fact]
    public async Task Each_real_estate_event_falls_on_its_day_within_the_60_days()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app));
        await PostAsync(app, app.AdultA, home, "valuations", new { asOf = "2024-09-20", amount = "300000" }); // yearly: stale from 2025-10-26
        var year = new { count = 1, unit = "year" };
        await PostAsync(app, app.AdultA, home, "recurring-costs", new { category = "property_tax", amount = "1200", every = year, startsOn = "2020-10-20" });
        await PostAsync(app, app.AdultA, home, "recurring-costs", new { category = "energy", amount = "100", every = year, startsOn = "2020-12-14" }); // today + 60: in
        await PostAsync(app, app.AdultA, home, "recurring-costs", new { category = "maintenance", amount = "80", every = year, startsOn = "2020-12-15" }); // today + 61: out
        await PostAsync(app, app.AdultA, home, "tenancies", new { label = "T2 meublé", startsOn = "2021-01-01", endsOn = "2025-12-01", rentMonthly = "900" });
        var loan = Short(app, "Prêt travaux");
        loan["principal"] = "11000";
        loan["totalPayments"] = 11;
        // Ten payments at 0 %, then the rate changes for the 11th and last, on 2025-11-15.
        loan["phases"] = new[] { Phase(annualRate: "0", payments: 10), Phase(seq: 2, annualRate: "0.01") };
        await NewAsync(app, loan);

        var events = await UpcomingAsync(app, app.AdultA);

        Assert.Equal(
            new[]
            {
                "2025-10-20 property_cost_due properties Maison property_tax 1200.00",
                "2025-10-26 valuation_due properties Maison - -",
                "2025-11-15 loan_end loans Prêt travaux - -",
                "2025-11-15 loan_phase_end loans Prêt travaux 1 -",
                "2025-12-01 tenancy_end properties Maison T2 meublé -",
                "2025-12-14 property_cost_due properties Maison energy 100.00",
            },
            events.Select(Line));
    }

    [Theory]
    [InlineData("household")]
    [InlineData("me")]
    public async Task An_unreadable_property_gives_no_event_and_a_stale_estimate_no_valuation_due(string scope)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var secret = await NewPropertyAsync(app, Home(app, "Secret", "private", app.AdultB), app.AdultB);
        await PostAsync(app, app.AdultB, secret, "recurring-costs",
            new { category = "property_tax", amount = "1200", every = new { count = 1, unit = "year" }, startsOn = "2020-10-20" });
        var old = await NewPropertyAsync(app, Home(app, "Ancienne"));
        await PostAsync(app, app.AdultA, old, "valuations", new { asOf = "2023-06-01", amount = "250000" }); // stale for months: « À faire »

        Assert.Empty(await UpcomingAsync(app, app.AdultA, scope));
    }
}
