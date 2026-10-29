using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>Spec 3F2 §2: with the bank as the reference, every reader of a loan follows its table — the loan, its property, the net worth.</summary>
public class BankScheduleReadingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<string> NetWorthAsync(NidApp app)
        => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/dashboard?scope=household")).RootElement
            .GetProperty("netWorth").GetProperty("total").GetProperty("amount").GetString()!;

    [Fact]
    public async Task The_loan_its_property_and_the_net_worth_follow_the_bank_once_it_is_the_reference()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{home}/valuations", new { asOf = "2025-06-01", amount = "290000" }));
        var body = Golden(app);
        body["propertyId"] = home;
        var loan = await NewAsync(app, body);

        await StoreTableAsync(app, loan, ScheduleSource.Computed, Sixtieth); // stored, not the reference
        Assert.Equal("162253.12", S((await DetailAsync(app, loan)).GetProperty("figures"), "balance"));
        Assert.Equal("127746.88", S((await PropertyAsync(app, home)).GetProperty("figures"), "netValue"));
        Assert.Equal("127746.88", await NetWorthAsync(app));

        await StoreTableAsync(app, loan, ScheduleSource.Bank, Sixtieth);

        Assert.Equal("161253.12", S((await DetailAsync(app, loan)).GetProperty("figures"), "balance"));
        var row = Assert.Single((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Loans}?scope=me"))).GetProperty("items").EnumerateArray());
        Assert.Equal("161253.12", S(row, "remainingBalance"));
        Assert.Equal("128746.88", S((await PropertyAsync(app, home)).GetProperty("figures"), "netValue"));
        Assert.Equal("128746.88", await NetWorthAsync(app));
        // After the table, the calculation from the bank's balance: 470.32 of interest, 689.60 repaid on the 61st payment.
        var rows = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Loans}/{loan}/schedule"))).GetProperty("rows");
        Assert.Equal(("161253.12", "470.32", "160563.52"), (S(rows[59], "balance"), S(rows[60], "interest"), S(rows[60], "balance")));
    }
}
