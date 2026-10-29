using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>Spec 3C1 §5–§7: the readable linked loans in a property's figures, their currencies, and what another adult never sees.</summary>
public class PropertyFiguresTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>README §12's purchase, estimated 290 000 on 2025-06-01.</summary>
    private static async Task<Guid> EstimatedAsync(NidApp app)
    {
        var id = await NewPropertyAsync(app, Home(app));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{id}/valuations", new { asOf = "2025-06-01", amount = "290000" }));
        return id;
    }

    private static Dictionary<string, object?> LinkedTo(Guid property, Dictionary<string, object?> loan)
    {
        loan["propertyId"] = property;
        return loan;
    }

    [Fact]
    public async Task Two_linked_loans_lower_the_net_value_and_their_interest_raises_the_real_cost()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await EstimatedAsync(app);
        var main = await NewAsync(app, LinkedTo(home, Golden(app)));     // 162 253.12 owed and 31 848.32 of interest at its 60th payment
        var zeroRate = await NewAsync(app, LinkedTo(home, Short(app)));  // 2 000 owed after 10 payments of 1 000, no interest

        var detail = await PropertyAsync(app, home);
        var loans = detail.GetProperty("loans").EnumerateArray().ToList();
        Assert.Equal([main, zeroRate], loans.Select(l => l.GetProperty("id").GetGuid())); // by name: « Prêt maison », « Prêt travaux »
        Assert.Equal(("Prêt maison", "EUR", "162253.12", "162253.12"),
            (S(loans[0], "name"), S(loans[0], "currency"), S(loans[0], "balance"), S(loans[0], "balanceConverted")));
        var figures = detail.GetProperty("figures");
        Assert.Equal(("164253.12", "125746.88"), (S(figures, "linkedBalance"), S(figures, "netValue")));
        Assert.Equal(("269000.00", "21000.00"), (S(figures, "costPrice"), S(figures, "latentGain")));
        Assert.Equal(("300848.32", "loan_interest", "31848.32"),
            (S(figures, "realCost"), S(figures.GetProperty("realCostLines")[1], "code"), S(figures.GetProperty("realCostLines")[1], "amount")));
        Assert.Empty(detail.GetProperty("warnings").EnumerateArray());

        var row = Assert.Single((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Properties))).GetProperty("items").EnumerateArray());
        Assert.Equal("125746.88", S(row, "netValue"));
    }

    [Fact]
    public async Task A_loan_in_another_currency_is_converted_and_one_without_a_rate_is_left_out()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IFxRateStore>()
                .WriteAsync([new FxRate(NidApp.GoldenCaseToday, Currency.Eur, 0.9m, FxRateSource.Ecb)], default); // 1 USD = 0.90 EUR
        var home = await EstimatedAsync(app);
        var dollars = Short(app, "Prêt dollars");
        dollars["currency"] = "USD";
        var pounds = Short(app, "Prêt livres");
        pounds["currency"] = "GBP"; // no GBP rate is stored
        var usd = await NewAsync(app, LinkedTo(home, dollars));
        var gbp = await NewAsync(app, LinkedTo(home, pounds));

        var detail = await PropertyAsync(app, home);
        var loans = detail.GetProperty("loans").EnumerateArray().ToList();
        Assert.Equal(("USD", "2000.00", "1800.00"), (S(loans[0], "currency"), S(loans[0], "balance"), S(loans[0], "balanceConverted")));
        Assert.Equal(("GBP", "2000.00", (string?)null), (S(loans[1], "currency"), S(loans[1], "balance"), S(loans[1], "balanceConverted")));
        var figures = detail.GetProperty("figures");
        Assert.Equal(("1800.00", "288200.00"), (S(figures, "linkedBalance"), S(figures, "netValue")));
        Assert.Equal([("loan_other_currency", usd), ("fx_missing", gbp)],
            detail.GetProperty("warnings").EnumerateArray().Select(w => (S(w, "code"), w.GetProperty("loanId").GetGuid())));
    }

    [Fact]
    public async Task A_private_loan_on_a_shared_property_is_invisible_to_the_other_adult_and_flagged_to_its_owner()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await EstimatedAsync(app); // shared, owned by A
        // An ASCII name: the JSON writer escapes accented letters, and the scan below must be able to find it.
        var body = LinkedTo(home, Short(app, "ZZLOANOFB"));
        body["visibility"] = "private";
        body["borrowers"] = new[] { new { personId = app.AdultB.Value, shareBp = 10_000 } };
        var hidden = await NewAsync(app, body, app.AdultB);

        var ofB = await PropertyAsync(app, home, app.AdultB);
        Assert.Equal(hidden, Only(ofB, "loans").GetProperty("id").GetGuid());
        var warning = Only(ofB, "warnings");
        Assert.Equal(("visibility_mismatch", hidden), (S(warning, "code"), warning.GetProperty("loanId").GetGuid()));
        Assert.Equal("288000.00", S(ofB.GetProperty("figures"), "netValue"));

        // A, who cannot read it: no id, no name, no total, no count — the property reads as if no loan were linked.
        var raw = await app.GetRawAsync(app.AdultA, $"{Properties}/{home}");
        Assert.DoesNotContain(hidden.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ZZLOANOFB", raw, StringComparison.Ordinal);
        var ofA = await PropertyAsync(app, home);
        Assert.Empty(ofA.GetProperty("loans").EnumerateArray());
        Assert.Empty(ofA.GetProperty("warnings").EnumerateArray());
        var figures = ofA.GetProperty("figures");
        Assert.Equal(("0.00", "290000.00", "269000.00"), (S(figures, "linkedBalance"), S(figures, "netValue"), S(figures, "realCost")));

        var rowOfA = Assert.Single((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Properties))).GetProperty("items").EnumerateArray());
        Assert.Equal("290000.00", S(rowOfA, "netValue"));
        Assert.DoesNotContain(hidden.ToString(), await app.GetRawAsync(app.AdultA, Properties), StringComparison.OrdinalIgnoreCase);
        var rowOfB = Assert.Single((await JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Get, Properties))).GetProperty("items").EnumerateArray());
        Assert.Equal("288000.00", S(rowOfB, "netValue"));
    }
}
