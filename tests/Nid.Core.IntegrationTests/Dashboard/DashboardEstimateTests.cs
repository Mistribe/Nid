using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.EnvelopeEstimateApi;

namespace Nid.Core.IntegrationTests.Dashboard;

/// <summary>
/// Holding prices spec §5.4: an envelope's estimate, when newer than its latest valuation, is its value today on the home dashboard.
/// The PEA's statement: the ETF 1 000 and the euro fund 500; at 110 (14/10) the ETF makes the estimate 1 600.
/// </summary>
public class DashboardEstimateTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<JsonElement> DashboardAsync(NidApp app, PersonId reader, string scope = "household")
        => JsonDocument.Parse(await app.GetRawAsync(reader, $"/api/v1/dashboard?scope={scope}")).RootElement;

    private static async Task<List<JsonElement>> PointsAsync(NidApp app, PersonId reader, string scope = "household")
        => JsonDocument.Parse(await app.GetRawAsync(reader, $"/api/v1/dashboard/net-worth?scope={scope}&period=12m")).RootElement
            .GetProperty("points").EnumerateArray().ToList();

    private static string? Total(JsonElement dashboard) => dashboard.GetProperty("netWorth").GetProperty("total").GetProperty("amount").GetString();

    private static string? Amount(JsonElement money) => money.ValueKind == JsonValueKind.Null ? null : money.GetProperty("amount").GetString();

    private static JsonElement At(List<JsonElement> points, string on) => points.Single(p => p.GetProperty("on").GetString() == on);

    private static bool AnyEstimated(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().Any(p => (p.Name == "estimated" && p.Value.ValueKind == JsonValueKind.True) || AnyEstimated(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Any(AnyEstimated),
        _ => false,
    };

    [Fact]
    public async Task Net_worth_counts_the_estimate_and_the_split_says_estimated()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _) = await EnvelopeAsync(app);
        await PriceAsync(app, EtfIsin, 110m, Day);

        var dashboard = await DashboardAsync(app, app.AdultA);

        Assert.Equal("1600.00", Total(dashboard));
        Assert.Equal("2025-10-14", dashboard.GetProperty("valuesAsOf").GetString());
        Assert.Equal(1, dashboard.GetProperty("estimatedEnvelopes").GetInt32());
        var row = Assert.Single(dashboard.GetProperty("envelopeSplit").EnumerateArray());
        Assert.Equal((id.Value, "1600.00", "2025-10-14", true, false), (row.GetProperty("id").GetGuid(), Amount(row.GetProperty("converted")),
            row.GetProperty("asOf").GetString(), row.GetProperty("estimated").GetBoolean(), row.GetProperty("stale").GetBoolean()));
    }

    [Fact]
    public async Task The_allocation_follows_the_estimated_lines_and_sums_to_the_estimate()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await EnvelopeAsync(app);
        await PriceAsync(app, EtfIsin, 110m, Day);

        var allocation = (await DashboardAsync(app, app.AdultA)).GetProperty("allocation");

        Assert.Equal("1600.00", Amount(allocation.GetProperty("assets")));
        var classes = allocation.GetProperty("classes").EnumerateArray().ToDictionary(l => l.GetProperty("key").GetString()!, l => Amount(l.GetProperty("amount")));
        // By the statement's weights the ETF would be 1 600 × 1 000 / 1 500 = 1 066.67; by the estimated lines it is 1 100.
        Assert.Equal(("1100.00", "500.00"), (classes["equity"], classes["capital_guaranteed"]));
    }

    [Fact]
    public async Task An_estimated_envelope_stays_stale()
    {
        await using var app = await NidApp.StartAsync(fixture);
        // A statement four months old: past the monthly cadence's 45 days.
        var (id, _) = await EnvelopeAsync(app, statement: "2025-06-15");
        await PriceAsync(app, EtfIsin, 110m, Day);

        var dashboard = await DashboardAsync(app, app.AdultA);

        Assert.Equal("1600.00", Total(dashboard));
        Assert.Equal((1, 1), (dashboard.GetProperty("staleEnvelopes").GetInt32(), dashboard.GetProperty("estimatedEnvelopes").GetInt32()));
        var row = Assert.Single(dashboard.GetProperty("envelopeSplit").EnumerateArray());
        Assert.Equal((true, true), (row.GetProperty("estimated").GetBoolean(), row.GetProperty("stale").GetBoolean()));
        // D17: « estimée au 14/10, valeur du 15/06 à mettre à jour ».
        Assert.Equal(("2025-10-14", "2025-06-15"), (row.GetProperty("asOf").GetString(), row.GetProperty("valuedOn").GetString()));
        var todo = Assert.Single(dashboard.GetProperty("todo").EnumerateArray(),
            t => t.GetProperty("kind").GetString() == "stale_value" && t.GetProperty("id").GetGuid() == id.Value);
        Assert.Equal("2025-06-15", todo.GetProperty("on").GetString());
    }

    [Fact]
    public async Task Only_today_uses_the_estimate()
    {
        await using var app = await NidApp.StartAsync(fixture);
        // A statement of 15/06: the 31/07, 31/08 and 30/09 points fall between it and today.
        await EnvelopeAsync(app, statement: "2025-06-15");
        await PriceAsync(app, EtfIsin, 110m, Day);

        var points = await PointsAsync(app, app.AdultA);

        Assert.Equal("1600.00", Amount(At(points, "2025-10-15").GetProperty("value")));
        Assert.Equal("1500.00", Amount(At(points, "2025-09-30").GetProperty("value")));
        // The contributions stay those of the valuation.
        Assert.Equal(Amount(At(points, "2025-09-30").GetProperty("contributions")), Amount(At(points, "2025-10-15").GetProperty("contributions")));
    }

    [Fact]
    public async Task Estimates_follow_visibility()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (shared, _) = await EnvelopeAsync(app);
        await EnvelopeAsync(app, visibility: Visibility.Private); // the same ISIN, that B cannot see
        await PriceAsync(app, EtfIsin, 110m, Day);

        // « Moi »: the shared envelopes and the reader's own private ones.
        var seen = await DashboardAsync(app, app.AdultB, "me");

        Assert.Equal("1600.00", Total(seen));
        Assert.Equal(1, seen.GetProperty("estimatedEnvelopes").GetInt32());
        Assert.Equal([shared.Value], seen.GetProperty("envelopeSplit").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
        Assert.Equal("1600.00", Amount(At(await PointsAsync(app, app.AdultB, "me"), "2025-10-15").GetProperty("value")));
        // A sees both, each estimated.
        var owner = await DashboardAsync(app, app.AdultA, "me");
        Assert.Equal(("3200.00", 2), (Total(owner), owner.GetProperty("estimatedEnvelopes").GetInt32()));
    }

    [Fact]
    public async Task Disabled_changes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await EnvelopeAsync(app, enable: false);
        var before = await app.GetRawAsync(app.AdultA, "/api/v1/dashboard?scope=household");
        var curveBefore = await app.GetRawAsync(app.AdultA, "/api/v1/dashboard/net-worth?scope=household&period=12m");

        await PriceAsync(app, EtfIsin, 110m, Day);

        Assert.Equal(before, await app.GetRawAsync(app.AdultA, "/api/v1/dashboard?scope=household"));
        Assert.Equal(curveBefore, await app.GetRawAsync(app.AdultA, "/api/v1/dashboard/net-worth?scope=household&period=12m"));
        var dashboard = JsonDocument.Parse(before).RootElement;
        Assert.Equal("1500.00", Total(dashboard));
        Assert.False(AnyEstimated(dashboard));
    }
}
