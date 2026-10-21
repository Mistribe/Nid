using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Investments;

/// <summary>Holdings spec (issue #34, PR 1): holdings and snapshots of an envelope through the API.</summary>
[Collection("postgres")]
public class HoldingEndpointTests(PostgresFixture fixture)
{
    private const string FundIsin = "FR0000120271";

    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string?> CodeAsync(HttpResponseMessage response) => (await JsonAsync(response)).GetProperty("code").GetString();

    private static string Url(ItemId id) => $"/api/v1/investments/envelopes/{id.Value}";

    private static async Task<Guid> AddAsync(NidApp app, ItemId id, object body)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holdings", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Issue #34 §8: a euro fund and fund A.</summary>
    private static async Task<(ItemId Id, Guid EuroFund, Guid FundA)> GoldenAsync(NidApp app)
    {
        var id = await app.NewEnvelopeAsync("Assurance-vie", app.AdultA, Visibility.Shared);
        var euro = await AddAsync(app, id, new { name = "Fonds euros", kind = "euro_fund", assetClass = "capital_guaranteed" });
        var fund = await AddAsync(app, id, new { name = "Fonds A", kind = "fund", isin = FundIsin.ToLowerInvariant(), riskIndicator = 4 });
        return (id, euro, fund);
    }

    private static object Snapshot(string asOf, Guid euro, string euroValue, Guid fund, string units, string price, string value, bool recordValuation = false)
        => new
        {
            asOf, sourceLabel = "Information annuelle", recordValuation,
            lines = new object[]
            {
                new { holdingId = euro, value = Eur(euroValue) },
                new { holdingId = fund, units, unitPrice = price, value = Eur(value) },
            },
        };

    [Fact]
    public async Task The_golden_snapshot_reads_its_lines_weights_and_unallocated()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, euro, fund) = await GoldenAsync(app);
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/valuations", new { asOf = "2024-12-31", amount = Eur("10733.40") });
        var recorded = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holding-snapshots",
            Snapshot("2024-12-31", euro, "3090.00", fund, "99.2", "77", "7638.40"));
        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        Assert.Equal("holdings_sum_mismatch", (await JsonAsync(recorded)).GetProperty("warnings")[0].GetProperty("code").GetString());

        var read = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url(id)}/holdings"));
        Assert.Equal("2024-12-31", read.GetProperty("holdingsAsOf").GetString());
        Assert.Equal("10728.4000", read.GetProperty("total").GetProperty("amount").GetString());
        Assert.Equal("5.0000", read.GetProperty("unallocated").GetProperty("amount").GetString());
        var lines = read.GetProperty("holdings").EnumerateArray().ToDictionary(h => h.GetProperty("id").GetGuid());
        Assert.Equal(FundIsin, lines[fund].GetProperty("isin").GetString());
        Assert.Equal(("99.200000", "77.000000", 71.2m), (lines[fund].GetProperty("line").GetProperty("units").GetString(),
            lines[fund].GetProperty("line").GetProperty("unitPrice").GetString(), lines[fund].GetProperty("line").GetProperty("weightPct").GetDecimal()));
        Assert.Equal(JsonValueKind.Null, lines[euro].GetProperty("line").GetProperty("units").ValueKind);
        Assert.Equal(28.8m, lines[euro].GetProperty("line").GetProperty("weightPct").GetDecimal());

        // Before the snapshot: every holding, no line.
        var before = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url(id)}/holdings?on=2024-06-30"));
        Assert.Equal(JsonValueKind.Null, before.GetProperty("holdingsAsOf").ValueKind);
        Assert.All(before.GetProperty("holdings").EnumerateArray(), h => Assert.Equal(JsonValueKind.Null, h.GetProperty("line").ValueKind));
    }

    [Fact]
    public async Task Recording_the_valuation_writes_the_total_and_a_same_date_snapshot_replaces_the_previous_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, euro, fund) = await GoldenAsync(app);
        var first = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holding-snapshots",
            Snapshot("2025-06-30", euro, "1.00", fund, "1", "1", "1.00")));
        var second = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holding-snapshots",
            Snapshot("2025-06-30", euro, "4806.70", fund, "78.4", "75", "5880.00", recordValuation: true)));

        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        Assert.Equal(0, second.GetProperty("warnings").GetArrayLength());
        var snapshots = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url(id)}/holding-snapshots"))).GetProperty("items");
        Assert.Equal("10686.7000", Assert.Single(snapshots.EnumerateArray()).GetProperty("total").GetProperty("amount").GetString());
        var envelope = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Url(id)));
        Assert.Equal(("10686.7000", "2025-06-30"), (envelope.GetProperty("figures").GetProperty("value").GetProperty("amount").GetString(),
            envelope.GetProperty("figures").GetProperty("asOf").GetString()));

        // A PUT cannot move a snapshot onto another's date.
        var other = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holding-snapshots",
            Snapshot("2024-12-31", euro, "3090.00", fund, "99.2", "77", "7638.40")))).GetProperty("id").GetGuid();
        var moved = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url(id)}/holding-snapshots/{other}",
            Snapshot("2025-06-30", euro, "3090.00", fund, "99.2", "77", "7638.40"));
        Assert.Equal((HttpStatusCode.Conflict, "investments.snapshot_exists"), (moved.StatusCode, await CodeAsync(moved)));

        // A PUT with fewer lines drops the others; a DELETE removes it.
        var replaced = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url(id)}/holding-snapshots/{other}",
            new { asOf = "2024-12-30", complete = false, lines = new[] { new { holdingId = euro, value = Eur("3090.00") } } });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var kept = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url(id)}/holding-snapshots"))).GetProperty("items")[1];
        Assert.Equal(("2024-12-30", false, 1), (kept.GetProperty("asOf").GetString(), kept.GetProperty("complete").GetBoolean(), kept.GetProperty("lines").GetArrayLength()));
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Url(id)}/holding-snapshots/{other}")).StatusCode);
    }

    [Fact]
    public async Task A_line_off_its_units_times_price_warns_on_that_holding()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, euro, fund) = await GoldenAsync(app);

        var warnings = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holding-snapshots",
            Snapshot("2025-06-30", euro, "100.00", fund, "78.4", "75", "5890.00")))).GetProperty("warnings");

        var warning = Assert.Single(warnings.EnumerateArray());
        Assert.Equal(("holding_value_mismatch", fund), (warning.GetProperty("code").GetString(), warning.GetProperty("holdingId").GetGuid()));
    }

    [Fact]
    public async Task Units_printed_to_3_decimals_keep_their_room_once_stored()
    {
        // Issue #46: « tronqué à 3 décimales », 1.185 × 3 924.47 is 2.77 € below the printed value, within 0.001 × 3 924.47.
        await using var app = await NidApp.StartAsync(fixture);
        var (id, euro, fund) = await GoldenAsync(app);

        var recorded = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holding-snapshots",
            Snapshot("2025-06-30", euro, "100.00", fund, "1.185", "3924.47", "4653.27")));
        Assert.Empty(recorded.GetProperty("warnings").EnumerateArray());

        // Read back from the database: the decimals survive, and the wire still shows 6.
        var read = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url(id)}/holdings"));
        Assert.DoesNotContain("holding_value_mismatch", read.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()));
        var line = read.GetProperty("holdings").EnumerateArray().Single(h => h.GetProperty("id").GetGuid() == fund).GetProperty("line");
        Assert.Equal("1.185000", line.GetProperty("units").GetString());
    }

    [Theory]
    [InlineData("""{ "name": "X", "kind": "crypto" }""", "investments.unknown_holding_kind", "kind")]
    [InlineData("""{ "name": "X", "kind": "fund", "isin": "FR0000120272" }""", "investments.invalid_isin", "isin")]
    [InlineData("""{ "name": " ", "kind": "fund" }""", "investments.invalid_holding", "name")]
    [InlineData("""{ "name": "X", "kind": "fund", "riskIndicator": 8 }""", "investments.invalid_holding", "riskIndicator")]
    [InlineData("""{ "name": "X", "kind": "fund", "assetClass": "gold" }""", "investments.invalid_holding", "assetClass")]
    [InlineData("""{ "name": "X", "kind": "fund", "isin": "FR0000120271" }""", "investments.invalid_holding", "isin")] // already fund A's
    public async Task A_holding_out_of_its_rules_is_refused_at_its_field(string body, string code, string field)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _, _) = await GoldenAsync(app);

        var refused = await app.RawTextAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holdings", body);

        var json = await JsonAsync(refused);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, code, field),
            (refused.StatusCode, json.GetProperty("code").GetString(), json.GetProperty("errors")[0].GetProperty("path").GetString()));
    }

    [Fact]
    public async Task Snapshot_lines_out_of_their_rules_are_refused_at_their_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, euro, fund) = await GoldenAsync(app);
        var other = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var stranger = await AddAsync(app, other, new { name = "ETF", kind = "etf" });

        async Task Refused(object body, string code, string path)
        {
            var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holding-snapshots", body);
            var json = await JsonAsync(response);
            Assert.Equal((code, path), (json.GetProperty("code").GetString(), json.GetProperty("errors")[0].GetProperty("path").GetString()));
        }

        await Refused(new { asOf = "2025-06-30", lines = new[] { new { holdingId = euro, units = "1", value = Eur("1") } } }, "investments.invalid_snapshot", "lines[0].units");
        await Refused(new { asOf = "2025-06-30", lines = new[] { new { holdingId = stranger, value = Eur("1") } } }, "investments.invalid_snapshot", "lines[0].holdingId");
        await Refused(new { asOf = "2025-06-30", lines = new[] { new { holdingId = fund, units = "0.0000001", value = Eur("1") } } }, "investments.invalid_snapshot", "lines[0].units");
        await Refused(new { asOf = "2025-06-30", lines = new[] { new { holdingId = euro, value = new { amount = "1", currency = "USD" } } } }, "investments.invalid_snapshot", "lines[0].value");
        await Refused(new { asOf = "2025-10-16", lines = new[] { new { holdingId = euro, value = Eur("1") } } }, "investments.invalid_snapshot", "asOf");
        await Refused(new { asOf = "2025-06-30", lines = Array.Empty<object>() }, "investments.invalid_snapshot", "lines");
        await Refused(new { asOf = "2025-06-30", lines = new[] { new { holdingId = euro, value = Eur("1") }, new { holdingId = euro, value = Eur("2") } } },
            "investments.invalid_snapshot", "lines[1].holdingId");
    }

    [Fact]
    public async Task A_holding_in_a_snapshot_is_closed_rather_than_deleted_and_a_stale_version_conflicts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, euro, fund) = await GoldenAsync(app);
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holding-snapshots", Snapshot("2024-12-31", euro, "3090.00", fund, "99.2", "77", "7638.40"));
        uint VersionOf(JsonElement read, Guid holding)
            => read.GetProperty("holdings").EnumerateArray().Single(h => h.GetProperty("id").GetGuid() == holding).GetProperty("version").GetUInt32();
        var version = VersionOf(await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url(id)}/holdings")), fund);

        var deleted = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Url(id)}/holdings/{fund}?version={version}");
        Assert.Equal((HttpStatusCode.Conflict, "investments.holding_in_use"), (deleted.StatusCode, await CodeAsync(deleted)));

        var closed = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url(id)}/holdings/{fund}",
            new { name = "Fonds A", kind = "fund", isin = FundIsin, closedOn = "2025-06-30", version });
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        var stale = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url(id)}/holdings/{fund}", new { name = "Fonds A", kind = "fund", version });
        Assert.Equal((HttpStatusCode.Conflict, "investments.holding_conflict"), (stale.StatusCode, await CodeAsync(stale)));

        var after = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holding-snapshots",
            new { asOf = "2025-07-31", lines = new[] { new { holdingId = fund, value = Eur("1") } } });
        Assert.Equal((HttpStatusCode.Conflict, "investments.holding_closed"), (after.StatusCode, await CodeAsync(after)));

        var unused = await AddAsync(app, id, new { name = "Liquidités", kind = "cash" });
        var unusedVersion = VersionOf(await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url(id)}/holdings")), unused);
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Url(id)}/holdings/{unused}?version={unusedVersion}")).StatusCode);
    }

    [Fact]
    public async Task Holdings_follow_the_envelopes_access_and_closing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await app.NewEnvelopeAsync("Secret", app.AdultA, Visibility.Private);
        var (shared, euro, _) = await GoldenAsync(app);

        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Url(hidden)}/holdings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Url(shared)}/holdings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await app.RawAsync(app.AdultB, HttpMethod.Post, $"{Url(shared)}/holdings", new { name = "X", kind = "fund" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Url(hidden)}/holdings/{euro}?version=1")).StatusCode);

        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(shared)}/close", new { closedOn = "2025-09-30" })).EnsureSuccessStatusCode();
        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(shared)}/holdings", new { name = "X", kind = "fund" });
        Assert.Equal((HttpStatusCode.Conflict, "investments.envelope_closed"), (refused.StatusCode, await CodeAsync(refused)));
    }
}
