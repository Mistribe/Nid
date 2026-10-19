using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Investments;

/// <summary>Issue #34 (PR 3): cost reports, unexplained units, the units-of-account floor and maturities through the API (today: 2025-10-15).</summary>
[Collection("postgres")]
public class HoldingCostEndpointTests(PostgresFixture fixture)
{
    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static string Url(ItemId id) => $"/api/v1/investments/envelopes/{id.Value}";

    private static async Task<JsonElement> SendAsync(NidApp app, HttpMethod method, string url, object? body, HttpStatusCode expected)
    {
        var response = await app.RawAsync(app.AdultA, method, url, body);
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return response.Content.Headers.ContentLength == 0 ? default : await JsonAsync(response);
    }

    private static async Task Refused(NidApp app, HttpMethod method, string url, object body, string code, string field)
    {
        var problem = await SendAsync(app, method, url, body, HttpStatusCode.UnprocessableEntity);
        Assert.Equal((code, field), (problem.GetProperty("code").GetString(), problem.GetProperty("errors")[0].GetProperty("path").GetString()));
    }

    private static string[] Codes(JsonElement warnings) => [.. warnings.EnumerateArray().Select(w => w.GetProperty("code").GetString()!)];

    /// <summary>Issue #34 §8 a year earlier, with the 2024 fee movement missing: two snapshots and the switch.</summary>
    private static async Task<(ItemId Id, Guid EuroFund, Guid FundA)> GoldenWithoutTheFeeAsync(NidApp app)
    {
        var id = await app.NewEnvelopeAsync("Assurance-vie", app.AdultA, Visibility.Shared);
        Guid Created(JsonElement x) => x.GetProperty("id").GetGuid();
        var euro = Created(await SendAsync(app, HttpMethod.Post, $"{Url(id)}/holdings", new { name = "Fonds euros", kind = "euro_fund" }, HttpStatusCode.Created));
        var fund = Created(await SendAsync(app, HttpMethod.Post, $"{Url(id)}/holdings", new { name = "Fonds A", kind = "fund" }, HttpStatusCode.Created));
        await SendAsync(app, HttpMethod.Post, $"{Url(id)}/holding-snapshots", new
        {
            asOf = "2023-12-31", recordValuation = true,
            lines = new object[] { new { holdingId = euro, value = Eur("3090.00") }, new { holdingId = fund, units = "99.2", unitPrice = "77", value = Eur("7638.40") } },
        }, HttpStatusCode.Created);
        await SendAsync(app, HttpMethod.Post, $"{Url(id)}/switches", new
        {
            occurredOn = "2024-06-30", from = new { holdingId = fund, amount = Eur("1600.00"), units = "-20", unitPrice = "80" },
            to = new { holdingId = euro, amount = Eur("1600.00") },
        }, HttpStatusCode.Created);
        await SendAsync(app, HttpMethod.Post, $"{Url(id)}/holding-snapshots", new
        {
            asOf = "2024-12-31", recordValuation = true,
            lines = new object[] { new { holdingId = euro, value = Eur("4806.70") }, new { holdingId = fund, units = "78.4", unitPrice = "75", value = Eur("5880.00") } },
        }, HttpStatusCode.Created);
        return (id, euro, fund);
    }

    [Fact]
    public async Task A_cost_report_is_saved_as_stated_with_the_estimated_implicit_cost_and_replaced_by_year()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _, fund) = await GoldenWithoutTheFeeAsync(app);
        var url = $"{Url(id)}/holdings/{fund}/cost-reports/2024";

        var saved = await SendAsync(app, HttpMethod.Put, url,
            new { grossReturnPct = 8.00m, totalFeesPct = 2.50m, netReturnPct = 5.50m, retrocessionPct = 0.85m, sourceLabel = "Relevé annuel 2024" }, HttpStatusCode.OK);
        Assert.Equal(("168.98", 0), (saved.GetProperty("estimatedImplicitCost").GetProperty("amount").GetString(), saved.GetProperty("warnings").GetArrayLength()));

        var replaced = await SendAsync(app, HttpMethod.Put, url, new { grossReturnPct = 8.00m, totalFeesPct = 2.50m, netReturnPct = 5.40m }, HttpStatusCode.OK);
        Assert.Equal(["cost_report_inconsistent"], Codes(replaced.GetProperty("warnings")));
        var read = await SendAsync(app, HttpMethod.Get, url, null, HttpStatusCode.OK);
        Assert.Equal((5.40m, JsonValueKind.Null, JsonValueKind.Null), (read.GetProperty("netReturnPct").GetDecimal(),
            read.GetProperty("retrocessionPct").ValueKind, read.GetProperty("sourceLabel").ValueKind));

        // The first year has no value at its start: no estimate.
        var first = await SendAsync(app, HttpMethod.Put, $"{Url(id)}/holdings/{fund}/cost-reports/2023",
            new { grossReturnPct = 12m, totalFeesPct = 2m, netReturnPct = 10m }, HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("estimatedImplicitCost").ValueKind);
        await SendAsync(app, HttpMethod.Get, $"{Url(id)}/holdings/{fund}/cost-reports/2022", null, HttpStatusCode.NotFound);
        var list = (await SendAsync(app, HttpMethod.Get, $"{Url(id)}/holdings/{fund}/cost-reports", null, HttpStatusCode.OK)).GetProperty("items");
        Assert.Equal([2024, 2023], list.EnumerateArray().Select(r => r.GetProperty("year").GetInt32()));
        Assert.Equal("168.98", list[0].GetProperty("estimatedImplicitCost").GetProperty("amount").GetString());
    }

    [Fact]
    public async Task A_cost_report_is_refused_out_of_bounds_and_on_another_envelopes_holding()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _, fund) = await GoldenWithoutTheFeeAsync(app);
        var url = $"{Url(id)}/holdings/{fund}/cost-reports";

        await Refused(app, HttpMethod.Put, $"{url}/2026", new { grossReturnPct = 1m, totalFeesPct = 1m, netReturnPct = 0m }, "investments.invalid_cost_report", "year");
        await Refused(app, HttpMethod.Put, $"{url}/2024", new { totalFeesPct = 1m, netReturnPct = 0m }, "investments.invalid_cost_report", "grossReturnPct");
        await Refused(app, HttpMethod.Put, $"{url}/2024", new { grossReturnPct = 1m, totalFeesPct = -1m, netReturnPct = 2m }, "investments.invalid_cost_report", "totalFeesPct");
        await Refused(app, HttpMethod.Put, $"{url}/2024", new { grossReturnPct = 1m, totalFeesPct = 1m, netReturnPct = 0m, supportFeesPct = 0.12345m },
            "investments.invalid_cost_report", "supportFeesPct");

        var other = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        await SendAsync(app, HttpMethod.Put, $"{Url(other)}/holdings/{fund}/cost-reports/2024",
            new { grossReturnPct = 1m, totalFeesPct = 1m, netReturnPct = 0m }, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Missing_units_and_a_share_below_the_floor_warn_on_the_holdings_and_the_routine()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _, fund) = await GoldenWithoutTheFeeAsync(app);

        var unset = await SendAsync(app, HttpMethod.Get, $"{Url(id)}/holdings", null, HttpStatusCode.OK);
        Assert.Equal(55.0m, unset.GetProperty("ucSharePct").GetDecimal());
        Assert.Equal(["unexplained_units"], Codes(unset.GetProperty("warnings")));
        var missing = unset.GetProperty("warnings")[0];
        Assert.Equal((fund, "2024-12-31", "-0.800000", "60.00"), (missing.GetProperty("holdingId").GetGuid(), missing.GetProperty("on").GetString(),
            missing.GetProperty("units").GetString(), missing.GetProperty("proposedFee").GetProperty("amount").GetString()));

        await Refused(app, HttpMethod.Patch, Url(id), new { ucShareFloorPct = 101m }, "investments.invalid_envelope", "ucShareFloorPct");
        await SendAsync(app, HttpMethod.Patch, Url(id), new { ucShareFloorPct = 70m }, HttpStatusCode.OK);
        Assert.Equal(70m, (await SendAsync(app, HttpMethod.Get, Url(id), null, HttpStatusCode.OK)).GetProperty("ucShareFloorPct").GetDecimal());
        var below = await SendAsync(app, HttpMethod.Get, $"{Url(id)}/holdings", null, HttpStatusCode.OK);
        Assert.Equal(["uc_share_below_floor", "unexplained_units"], Codes(below.GetProperty("warnings")));

        var routine = (await SendAsync(app, HttpMethod.Get, "/api/v1/investments/routine?on=2024-12-31", null, HttpStatusCode.OK))
            .GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == id.Value);
        Assert.Equal((70m, 55.0m, true), (routine.GetProperty("ucShareFloorPct").GetDecimal(), routine.GetProperty("ucSharePct").GetDecimal(),
            routine.GetProperty("ucShareBelowFloor").GetBoolean()));

        await SendAsync(app, HttpMethod.Patch, Url(id), new { clearUcShareFloorPct = true }, HttpStatusCode.OK);
        var cleared = await SendAsync(app, HttpMethod.Get, $"{Url(id)}/holdings", null, HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("ucShareFloorPct").ValueKind);
        Assert.Equal(["unexplained_units"], Codes(cleared.GetProperty("warnings")));
    }

    [Fact]
    public async Task An_open_holding_maturing_within_60_days_is_an_upcoming_event()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Assurance-vie", app.AdultA, Visibility.Shared);
        foreach (var (name, maturesOn, closedOn) in new (string, string, string?)[]
                 {
                     ("Autocall Europe", "2025-12-14", null), // today + 60: in
                     ("Note Stellantis", "2025-12-15", null), // today + 61: out
                     ("Autocall rappelé", "2025-11-01", "2025-06-30"), // closed: out
                 })
            await SendAsync(app, HttpMethod.Post, $"{Url(id)}/holdings", new { name, kind = "structured", maturesOn, closedOn }, HttpStatusCode.Created);

        var events = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/dashboard?scope=household")).RootElement
            .GetProperty("upcoming").EnumerateArray().Where(e => e.GetProperty("module").GetString() == "investments")
            .Select(e => $"{e.GetProperty("on").GetString()} {e.GetProperty("kind").GetString()} {e.GetProperty("name").GetString()} {e.GetProperty("label").GetString()}");

        Assert.Equal(["2025-12-14 holding_maturity Assurance-vie Autocall Europe"], events);
    }
}
