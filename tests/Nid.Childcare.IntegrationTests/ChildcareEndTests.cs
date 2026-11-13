using System.Globalization;
using System.Net;
using Nid.TestSupport;
using static Nid.Childcare.IntegrationTests.ChildcareApi;

namespace Nid.Childcare.IntegrationTests;

/// <summary>The end of the contract (IDCC 3239 art. 119–125): what it means before it is ended, then the last month's settlement. Today is 2025-10-15.</summary>
public class ChildcareEndTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static decimal Amount(System.Text.Json.JsonElement e, string name) => decimal.Parse(e.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);

    [Fact]
    public async Task Preview_gives_notice_and_indemnity()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = await CreateAsync(app);

        var end = await GetAsync(app, $"{url}/end?reason=withdrawal&notified=2025-10-01");
        Assert.Equal(1, end.GetProperty("seniorityMonths").GetInt32());
        Assert.Equal("2025-10-08", end.GetProperty("noticeEnd").GetString());
        Assert.False(end.GetProperty("indemnityDue").GetBoolean());
        Assert.False(end.GetProperty("grossRateKnown").GetBoolean());
        Assert.True((await GetAsync(app, $"{url}/end?reason=child_death&notified=2025-10-01")).GetProperty("indemnityDue").GetBoolean());

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{url}/end?reason=whim&notified=2025-10-01");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
    }

    [Fact]
    public async Task Last_month_settles_the_contract_and_keeps_it_once_validated()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = await CreateAsync(app);
        await ValidateAsync(app, url, "2025-09");
        Assert.Equal(HttpStatusCode.OK,
            (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/close", new { on = "2025-10-31", reason = "withdrawal", notified = "2025-10-01" })).StatusCode);
        Assert.Equal("withdrawal", (await GetAsync(app, url)).GetProperty("end").GetProperty("reason").GetString());

        var figures = (await GetAsync(app, $"{url}/statements/2025-10")).GetProperty("figures");
        // Every week welcomed since September: more hours than the mensualisation paid.
        Assert.True(Amount(figures.GetProperty("regularisation"), "amount") > 0m);
        var declaration = figures.GetProperty("declaration");
        Assert.Equal(5, declaration.GetProperty("paidLeaveDays").GetInt32()); // September and October: 2 × 2.5 days
        Assert.True(Amount(declaration, "leaveIndemnity") > 0m);
        Assert.Equal(0m, Amount(declaration, "ruptureIndemnity")); // a month of care

        var validated = (await ValidateAsync(app, url, "2025-10")).GetProperty("figures");
        Assert.Equal(figures.GetProperty("regularisation").GetProperty("amount").GetString(), validated.GetProperty("regularisation").GetProperty("amount").GetString());
        Assert.Equal(declaration.GetProperty("leaveIndemnity").GetString(), validated.GetProperty("declaration").GetProperty("leaveIndemnity").GetString());

        var settlement = await GetAsync(app, $"{url}/settlement");
        Assert.True(settlement.GetProperty("validated").GetBoolean());
        var lines = settlement.GetProperty("lines");
        Assert.Equal(Amount(declaration, "netPay") + Amount(declaration, "leaveIndemnity") + Amount(declaration, "upkeep") + Amount(declaration, "meals"),
            Amount(lines.GetProperty("total"), "amount"));
    }

    [Fact]
    public async Task No_settlement_before_the_end()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = await CreateAsync(app);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{url}/settlement")).StatusCode);
    }

    [Fact]
    public async Task Breach_indemnity_needs_the_gross_rate()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = await CreateAsync(app);
        await ValidateAsync(app, url, "2025-09");
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/close", new { on = "2025-10-31", reason = "child_death", notified = "2025-10-20" });
        var missing = await GetAsync(app, $"{url}/statements/2025-10");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, missing.GetProperty("figures").ValueKind);
        Assert.Contains(missing.GetProperty("problems").EnumerateArray(), p => p.GetProperty("code").GetString() == "childcare.gross_rate_required");

        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/reopen");
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/close",
            new { on = "2025-10-31", reason = "child_death", notified = "2025-10-20", grossHourlyRate = "5.50" })).StatusCode);
        var declaration = (await GetAsync(app, $"{url}/statements/2025-10")).GetProperty("figures").GetProperty("declaration");
        Assert.True(Amount(declaration, "ruptureIndemnity") > 0m);
    }
}
