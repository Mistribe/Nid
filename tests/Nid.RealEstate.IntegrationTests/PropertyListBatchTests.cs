using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>Batched reads spec §3: the list reads its properties as one block, and each row still reads as its detail.</summary>
public class PropertyListBatchTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<Guid> EstimatedAsync(NidApp app, Dictionary<string, object?> body, string amount, PersonId? actor = null)
    {
        var id = await NewPropertyAsync(app, body, actor);
        await CreatedAsync(await app.RawAsync(actor ?? app.AdultA, HttpMethod.Post, $"{Properties}/{id}/valuations", new { asOf = "2025-06-01", amount }));
        return id;
    }

    private static Dictionary<string, object?> LinkedTo(Guid property, Dictionary<string, object?> loan)
    {
        loan["propertyId"] = property;
        return loan;
    }

    private static async Task<List<JsonElement>> RowsAsync(NidApp app, PersonId reader, string scope)
    {
        var response = await app.RawAsync(reader, HttpMethod.Get, $"{Properties}?scope={scope}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("items").EnumerateArray().ToList();
    }

    [Theory]
    [InlineData("household")]
    [InlineData("me")]
    public async Task Each_reader_lists_only_their_properties_when_the_other_adult_holds_private_ones(string scope)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await EstimatedAsync(app, Home(app), "290000"); // shared, owned by A
        var secret = await EstimatedAsync(app, Home(app, "Secret", "private", app.AdultB), "100000", app.AdultB);
        var loan = LinkedTo(home, Short(app, "Prêt de B")); // 2 000 owed today
        loan["visibility"] = "private";
        loan["borrowers"] = new[] { new { personId = app.AdultB.Value, shareBp = 10_000 } };
        await NewAsync(app, loan, app.AdultB);

        // A reads neither B's property nor B's loan: a hidden id in A's block would refuse the whole list.
        var ofA = Assert.Single(await RowsAsync(app, app.AdultA, scope));
        Assert.Equal((home, (string?)"290000.00"), (ofA.GetProperty("id").GetGuid(), S(ofA, "netValue")));

        var expected = scope == "me"
            ? new (Guid, string?)[] { (home, "288000.00"), (secret, "100000.00") }
            : new (Guid, string?)[] { (home, "288000.00") };
        Assert.Equal(expected, (await RowsAsync(app, app.AdultB, scope)).Select(r => (r.GetProperty("id").GetGuid(), S(r, "netValue"))));
    }

    [Fact]
    public async Task Each_row_reads_as_its_detail_when_one_loan_converts_and_another_lacks_its_rate()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IFxRateStore>()
                .WriteAsync([new FxRate(NidApp.GoldenCaseToday, Currency.Eur, 0.9m, FxRateSource.Ecb)], default); // 1 USD = 0.90 EUR
        var withDollars = await EstimatedAsync(app, Home(app, "Avec dollars"), "290000");
        var withPounds = await EstimatedAsync(app, Home(app, "Avec livres"), "290000");
        var inDollars = Home(app, "En dollars");
        inDollars["currency"] = "USD";
        var american = await EstimatedAsync(app, inDollars, "290000");
        var plain = await EstimatedAsync(app, Home(app, "Sans crédit"), "290000");
        var dollars = Short(app, "Prêt dollars");
        dollars["currency"] = "USD";
        var pounds = Short(app, "Prêt livres");
        pounds["currency"] = "GBP"; // no GBP rate is stored
        await NewAsync(app, LinkedTo(withDollars, dollars));
        await NewAsync(app, LinkedTo(withPounds, pounds));
        await NewAsync(app, LinkedTo(american, Short(app, "Prêt euros")));

        var rows = await RowsAsync(app, app.AdultA, "household");

        Assert.Equal(4, rows.Count);
        foreach (var row in rows)
            Assert.Equal(S((await PropertyAsync(app, row.GetProperty("id").GetGuid())).GetProperty("figures"), "netValue"), S(row, "netValue"));
        var netValues = rows.ToDictionary(r => r.GetProperty("id").GetGuid(), r => S(r, "netValue"));
        Assert.Equal("288200.00", netValues[withDollars]); // 2 000 USD at 0.90
        Assert.Equal("290000.00", netValues[withPounds]);  // its loan left out, as the detail leaves it
        Assert.Equal("290000.00", netValues[plain]);
    }
}
