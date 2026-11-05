using System.Globalization;
using System.Net;
using System.Text.Json;
using Nid.Core.Application.Scenarios;
using Nid.Core.Contracts;
using Nid.Core.Domain.Tax;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.Core.IntegrationTests.Scenarios;

/// <summary>Issue #416: the sale scenario through the real pipeline and API. Today is 2025-10-15.</summary>
public class SaleScenarioTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Url = "/api/v1/scenarios/sale";

    private static decimal D(string amount) => decimal.Parse(amount, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>A's rental bought 250 000 € + 19 000 € of fees on 2020-06-15, valued 300 000 €, with the golden loan on it.</summary>
    private static async Task<(Guid Home, Guid Loan)> RentalWithLoanAsync(NidApp app, string type = "rental", string visibility = "shared", PersonId? owner = null)
    {
        var body = Home(app, visibility: visibility, owners: owner is { } o ? [o] : []);
        body["type"] = type;
        var home = await NewPropertyAsync(app, body, owner);
        await CreatedAsync(await app.RawAsync(owner ?? app.AdultA, HttpMethod.Post, $"{Properties}/{home}/valuations", new { asOf = "2025-06-01", amount = "300000" }));
        var loan = Golden(app, visibility: visibility, borrowers: owner is { } b ? [b] : []);
        loan["propertyId"] = home;
        return (home, await NewAsync(app, loan, owner));
    }

    private static object Document(Guid property, string saleOn = "2026-10-01", string price = "320000") => new
    {
        version = 1, property, saleOn, price, agencyFees = "10000", penalties = "2000", horizonYears = 10,
    };

    private static object Body(NidApp app, object document, string visibility = "shared") => new
    {
        name = "Vente", visibility, scope = "household", owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } }, document,
    };

    [Fact]
    public async Task The_seller_nets_the_engines_tax_the_loans_balance_and_the_penalties()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (home, loan) = await RentalWithLoanAsync(app);

        var result = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url}/simulate", new { scope = "household", document = Document(home) }));

        // The sale engine of « Impôt si vente », on the price less the agency fees, at the sale's date.
        var expected = PropertySale.Compute(new PropertySaleInput(PropertySaleKind.Rental, new LocalDate(2020, 6, 15), 250_000m, 19_000m, 0m, 310_000m,
            [new SaleOwner(app.AdultA.Value, 10_000)], new LocalDate(2026, 10, 1)), SaleTaxRules.For(2026));
        var sale = result.GetProperty("sale");
        var tax = D(sale.GetProperty("tax").GetProperty("amount").GetString()!);
        Assert.Equal(expected.IncomeTax + expected.SocialTax + expected.Surtax, tax);
        Assert.True(tax > 0m);
        var page = result.GetProperty("tax");
        Assert.Equal("taxed", page.GetProperty("status").GetString());
        Assert.Contains("agency_fees_deducted", page.GetProperty("notes").EnumerateArray().Select(n => n.GetString()));

        // The loan is repaid at its balance on the sale's date: the last row before it.
        var rows = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/realestate/loans/{loan}/schedule"))).GetProperty("rows").EnumerateArray();
        var balance = D(rows.Last(r => S(r, "dueDate") == "2026-09-15").GetProperty("balance").GetString()!);
        Assert.Equal(balance, D(sale.GetProperty("loansRepaid").GetProperty("amount").GetString()!));
        Assert.Equal(320_000m - 10_000m - tax - balance - 2_000m, D(sale.GetProperty("netSeller").GetProperty("amount").GetString()!));
        Assert.True(D(sale.GetProperty("loanPayments").GetProperty("amount").GetString()!) > 0m);

        // With and without the sale start equal and part at the horizon by the gap.
        var points = result.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(points[0].GetProperty("without").GetProperty("amount").GetString(), points[0].GetProperty("with").GetProperty("amount").GetString());
        Assert.Equal(
            D(result.GetProperty("withAtHorizon").GetProperty("amount").GetString()!) - D(result.GetProperty("withoutAtHorizon").GetProperty("amount").GetString()!),
            D(result.GetProperty("gap").GetProperty("amount").GetString()!));
        Assert.Empty(result.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task A_primary_home_owes_no_tax_and_a_past_sale_is_taken_today()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (home, _) = await RentalWithLoanAsync(app, "primary_residence");

        var result = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url}/simulate", new { document = Document(home, "2025-01-01") }));

        Assert.Equal("0.00", result.GetProperty("sale").GetProperty("tax").GetProperty("amount").GetString());
        Assert.Equal("2025-10-15", result.GetProperty("sale").GetProperty("soldOn").GetString());
        Assert.Equal("exempt", result.GetProperty("tax").GetProperty("status").GetString());
        Assert.Equal(["sale_in_past"], result.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task A_sale_is_created_listed_compared_replaced_and_deleted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (home, _) = await RentalWithLoanAsync(app);

        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, Url, Body(app, Document(home)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonOf(created)).GetProperty("id").GetGuid();

        var read = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url}/{id}"));
        Assert.Equal(home, read.GetProperty("document").GetProperty("property").GetGuid());
        var gap = read.GetProperty("result").GetProperty("gap").GetProperty("amount").GetString();

        // The other kinds' routes do not answer for it.
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/scenarios/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/scenarios/projection/{id}")).StatusCode);

        var row = (await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/scenarios"))).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == id);
        Assert.Equal(("property_sale", "320000.00", gap), (row.GetProperty("kind").GetString(), row.GetProperty("price").GetProperty("amount").GetString(),
            row.GetProperty("gap").GetProperty("amount").GetString()));

        // « Comparer »: the same sale on the same 10-year grid gives the same gap.
        var compared = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/scenarios/compare?scope=household&ids={id}"));
        var scenario = Assert.Single(compared.GetProperty("scenarios").EnumerateArray());
        Assert.Equal(("property_sale", gap), (scenario.GetProperty("kind").GetString(), scenario.GetProperty("gapNominal").GetString()));

        var replaced = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url}/{id}", Body(app, Document(home, price: "330000"))));
        Assert.Equal("330000.00", replaced.GetProperty("document").GetProperty("price").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"/api/v1/scenarios/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url}/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_property_out_of_sight_is_refused_on_write_and_hidden_on_read()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (hidden, _) = await RentalWithLoanAsync(app, visibility: "private", owner: app.AdultB);

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, Url, Body(app, Document(hidden)));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, SaleScenarioReader.PropertyNotFound, "property"), await RefusalAsync(refused));

        // B's shared scenario on B's private home (scope « Moi »): A reads it without the property, its figures nor its tax.
        var created = await app.RawAsync(app.AdultB, HttpMethod.Post, Url, new
        {
            name = "Vente", visibility = "shared", scope = "me", owners = new[] { new { personId = app.AdultB.Value, shareBp = 10_000 } },
            document = Document(hidden),
        });
        var id = (await JsonOf(created)).GetProperty("id").GetGuid();
        var read = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url}/{id}");
        var body = await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.DoesNotContain(hidden.ToString(), body);
        var result = JsonDocument.Parse(body).RootElement.GetProperty("result");
        Assert.Equal(JsonValueKind.Null, result.GetProperty("sale").ValueKind);
        Assert.Equal(["property_hidden"], result.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()));
    }
}
