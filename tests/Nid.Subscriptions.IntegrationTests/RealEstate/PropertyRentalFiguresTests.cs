using System.Text.Json;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.RealEstate.LoanApi;
using static Nid.Subscriptions.IntegrationTests.RealEstate.PropertyApi;

namespace Nid.Subscriptions.IntegrationTests.RealEstate;

/// <summary>Spec 3C2 §4: a property's rental figures in its detail and the list, over the 12 rolling months before the host's today (2025-10-15), the arrears over the rent ledger.</summary>
public class PropertyRentalFiguresTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task PostAsync(NidApp app, Guid property, string route, object body)
        => await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/{route}", body));

    private static async Task<Guid> RentalAsync(NidApp app)
    {
        var body = Home(app);
        body["type"] = "rental";
        return await NewPropertyAsync(app, body);
    }

    [Fact]
    public async Task README_section_12s_rent_gives_a_gross_yield_of_0_0384_in_the_detail_and_the_list() // Review Focus #5
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await RentalAsync(app);
        await PostAsync(app, id, "flows", new { occurredOn = "2021-03-01", kind = "expense", amount = "12000", category = "works" });
        await PostAsync(app, id, "tenancies", new { label = "T3", startsOn = "2020-09-01", rentMonthly = "900" });
        foreach (var occurredOn in new[]
                 {
                     "2024-11-05", "2024-12-05", "2025-01-05", "2025-02-05", "2025-03-05", "2025-04-05",
                     "2025-05-05", "2025-06-05", "2025-07-05", "2025-08-05", "2025-09-05", "2025-10-05",
                 })
            await PostAsync(app, id, "flows", new { occurredOn, kind = "income", amount = "900", category = "rent" });
        await PostAsync(app, id, "flows", new { occurredOn = "2025-09-20", kind = "expense", amount = "1500", category = "property_tax" });

        var detail = await PropertyAsync(app, id);
        var figures = detail.GetProperty("figures");
        // Arrears follow the rent ledger from the first rent (November 2024) to October, paid on the 5th.
        Assert.Equal(("2020-09-01", "10800.00", "10800.00", "0.00"),
            (S(figures, "rentedSince"), S(figures, "expectedRent"), S(figures, "rentReceived"), S(figures, "arrears")));
        Assert.Equal((0, "0.0000"), (figures.GetProperty("vacantDays").GetInt32(), S(figures, "vacancyRate")));
        // 10 800 ÷ 281 000, and (10 800 − 1 500) ÷ 281 000: the works were in the cost price already.
        Assert.Equal(("0.0384", "0.0331"), (S(figures, "grossYield"), S(figures, "netYieldBeforeTax")));
        Assert.DoesNotContain(detail.GetProperty("warnings").EnumerateArray(), w => S(w, "code") == "rental_without_tenancy");

        var row = Assert.Single((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Properties))).GetProperty("items").EnumerateArray());
        Assert.Equal(("0.0384", "0.0000"), (S(row, "grossYield"), S(row, "vacancyRate")));
    }

    [Fact]
    public async Task A_rental_without_tenancy_is_warned_and_its_yields_wait_for_12_months_of_rental()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await RentalAsync(app);

        var detail = await PropertyAsync(app, id);
        Assert.Equal(["no_valuation", "rental_without_tenancy"], detail.GetProperty("warnings").EnumerateArray().Select(w => S(w, "code")));
        var figures = detail.GetProperty("figures");
        foreach (var name in new[] { "rentedSince", "vacantDays", "vacancyRate", "grossYield", "netYieldBeforeTax" })
            Assert.Equal(JsonValueKind.Null, figures.GetProperty(name).ValueKind);
        Assert.Equal(("0.00", "0.00"), (S(figures, "expectedRent"), S(figures, "arrears")));
        var row = Assert.Single((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Properties))).GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("grossYield").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("vacancyRate").ValueKind);

        // Let since January, nothing received: the warning goes, nine months and 15 days of October are expected, the yields still
        // wait; the ledger owes October whole.
        await PostAsync(app, id, "tenancies", new { label = "T3", startsOn = "2025-01-01", rentMonthly = "900" });
        detail = await PropertyAsync(app, id);
        Assert.Equal(["no_valuation"], detail.GetProperty("warnings").EnumerateArray().Select(w => S(w, "code")));
        figures = detail.GetProperty("figures");
        Assert.Equal(("2025-01-01", "8535.48", "9000.00", "0.0000"),
            (S(figures, "rentedSince"), S(figures, "expectedRent"), S(figures, "arrears"), S(figures, "vacancyRate")));
        Assert.Equal(JsonValueKind.Null, figures.GetProperty("grossYield").ValueKind);
    }
}
