using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Investments.IntegrationTests;

/// <summary>Issue #34 (PR 2): movements, switches and a holding's figures through the API.</summary>
public class HoldingMovementEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static string Url(ItemId id) => $"/api/v1/investments/envelopes/{id.Value}";

    private static async Task<JsonElement> PostAsync(NidApp app, string url, object body)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, url, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await JsonAsync(response);
    }

    private static async Task<(ItemId Id, Guid EuroFund, Guid FundA)> HoldingsAsync(NidApp app)
    {
        var id = await app.NewEnvelopeAsync("Assurance-vie", app.AdultA, Visibility.Shared);
        var euro = (await PostAsync(app, $"{Url(id)}/holdings", new { name = "Fonds euros", kind = "euro_fund" })).GetProperty("id").GetGuid();
        var fund = (await PostAsync(app, $"{Url(id)}/holdings", new { name = "Fonds A", kind = "fund" })).GetProperty("id").GetGuid();
        return (id, euro, fund);
    }

    private static object Move(string on, string kind, string amount, string? units = null, string? price = null, Guid? flow = null)
        => new { occurredOn = on, kind, amount = Eur(amount), units, unitPrice = price, envelopeFlowId = flow };

    /// <summary>Issue #34 §8, one year earlier (2023–2024: the same day counts) so every date is before the fixture's today.</summary>
    private static async Task<(ItemId Id, Guid EuroFund, Guid FundA)> GoldenAsync(NidApp app)
    {
        var (id, euro, fund) = await HoldingsAsync(app);
        var flow = (await PostAsync(app, $"{Url(id)}/flows", new { occurredOn = "2023-01-01", kind = "contribution", amount = Eur("10000.00") }))
            .GetProperty("id").GetGuid();
        await PostAsync(app, $"{Url(id)}/holdings/{euro}/movements", Move("2023-01-01", "allocation_in", "3000.00", flow: flow));
        Assert.Equal(0, (await PostAsync(app, $"{Url(id)}/holdings/{fund}/movements", Move("2023-01-01", "allocation_in", "7000.00", "100", "70", flow)))
            .GetProperty("warnings").GetArrayLength());
        await PostAsync(app, $"{Url(id)}/holdings/{euro}/movements", Move("2023-12-31", "interest", "90.00"));
        await PostAsync(app, $"{Url(id)}/holdings/{fund}/movements", Move("2023-12-31", "fee", "61.60", "-0.8", "77"));
        await PostAsync(app, $"{Url(id)}/holding-snapshots", new
        {
            asOf = "2023-12-31", recordValuation = true,
            lines = new object[] { new { holdingId = euro, value = Eur("3090.00") }, new { holdingId = fund, units = "99.2", unitPrice = "77", value = Eur("7638.40") } },
        });
        var switched = await PostAsync(app, $"{Url(id)}/switches", new
        {
            occurredOn = "2024-06-30", from = new { holdingId = fund, amount = Eur("1600.00"), units = "-20", unitPrice = "80" },
            to = new { holdingId = euro, amount = Eur("1600.00") },
        });
        Assert.Equal(0, switched.GetProperty("warnings").GetArrayLength());
        await PostAsync(app, $"{Url(id)}/holdings/{euro}/movements", Move("2024-12-31", "interest", "116.70"));
        await PostAsync(app, $"{Url(id)}/holdings/{fund}/movements", Move("2024-12-31", "fee", "60.00", "-0.8", "75"));
        await PostAsync(app, $"{Url(id)}/holding-snapshots", new
        {
            asOf = "2024-12-31", recordValuation = true,
            lines = new object[] { new { holdingId = euro, value = Eur("4806.70") }, new { holdingId = fund, units = "78.4", unitPrice = "75", value = Eur("5880.00") } },
        });
        return (id, euro, fund);
    }

    [Fact]
    public async Task The_golden_case_reads_each_holdings_figures_and_leaves_the_envelope_unchanged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, euro, fund) = await GoldenAsync(app);

        var read = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url(id)}/holdings"));
        Assert.Equal(("121.6000", 0), (read.GetProperty("explicitFees").GetProperty("amount").GetString(), read.GetProperty("warnings").GetArrayLength()));
        var figures = read.GetProperty("holdings").EnumerateArray().ToDictionary(h => h.GetProperty("id").GetGuid(), h => h.GetProperty("figures"));
        (string, string, string, decimal, decimal, string, string) Of(JsonElement f) => (
            f.GetProperty("moneyIn").GetProperty("amount").GetString()!, f.GetProperty("moneyOut").GetProperty("amount").GetString()!,
            f.GetProperty("gain").GetProperty("amount").GetString()!, f.GetProperty("gainPct").GetDecimal(), f.GetProperty("annualReturnPct").GetDecimal(),
            f.GetProperty("explicitFees").GetProperty("amount").GetString()!, f.GetProperty("gainBeforeFees").GetProperty("amount").GetString()!);
        Assert.Equal(("7000.0000", "1600.0000", "480.0000", 6.9m, 3.6m, "121.6000", "601.6000"), Of(figures[fund]));
        Assert.Equal(("4600.0000", "0", "206.7000", 4.5m, 3.0m, "0", "206.7000"), Of(figures[euro]));

        string Performance(JsonElement p) => p.GetProperty("performance").GetProperty("amount").GetString()!;
        Assert.Equal("-158.4000", Performance(await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get,
            $"{Url(id)}/holdings/{fund}/performance?from=2024-01-01&to=2024-12-31"))));
        Assert.Equal("116.7000", Performance(await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get,
            $"{Url(id)}/holdings/{euro}/performance?from=2024-01-01&to=2024-12-31"))));
        Assert.Equal("-41.7000", Performance(await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get,
            $"{Url(id)}/performance?from=2024-01-01&to=2024-12-31"))));

        var envelope = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Url(id)))).GetProperty("figures");
        Assert.Equal(("686.7000", "10000.0000", 3.4m), (envelope.GetProperty("gain").GetProperty("amount").GetString(),
            envelope.GetProperty("netContributions").GetProperty("amount").GetString(), envelope.GetProperty("annualReturnPct").GetDecimal()));
        Assert.Single((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Url(id)))).GetProperty("flows").EnumerateArray());

        var series = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url(id)}/holdings/{fund}/series"))).GetProperty("points");
        Assert.Equal([("2023-12-31", "7638.4000", "7000.0000"), ("2024-12-31", "5880.0000", "5400.0000")], series.EnumerateArray()
            .Select(p => (p.GetProperty("asOf").GetString(), p.GetProperty("value").GetProperty("amount").GetString(),
                p.GetProperty("netContributions").GetProperty("amount").GetString())));

        var movements = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url(id)}/holdings/{fund}/movements"))).GetProperty("items");
        Assert.Equal(["allocation_in", "fee", "switch_out", "fee"], movements.EnumerateArray().Select(m => m.GetProperty("kind").GetString()));
        Assert.Equal(("-20.000000", "80.000000"), (movements[2].GetProperty("units").GetString(), movements[2].GetProperty("unitPrice").GetString()));
        var euroSwitch = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url(id)}/holdings/{euro}/movements"))).GetProperty("items")
            .EnumerateArray().Single(m => m.GetProperty("kind").GetString() == "switch_in");
        Assert.Equal(movements[2].GetProperty("switchRef").GetString(), euroSwitch.GetProperty("switchRef").GetString());
    }

    [Fact]
    public async Task Allocations_off_the_contribution_and_an_unbalanced_maturity_warn()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, euro, fund) = await HoldingsAsync(app);
        await PostAsync(app, $"{Url(id)}/flows", new { occurredOn = "2025-01-10", kind = "contribution", amount = Eur("1000.00") });

        var allocated = await PostAsync(app, $"{Url(id)}/holdings/{fund}/movements", Move("2025-01-10", "allocation_in", "900.00"));
        var warning = Assert.Single(allocated.GetProperty("warnings").EnumerateArray());
        Assert.Equal(("allocation_mismatch", "2025-01-10"), (warning.GetProperty("code").GetString(), warning.GetProperty("on").GetString()));
        Assert.Equal(0, (await PostAsync(app, $"{Url(id)}/holdings/{euro}/movements", Move("2025-01-10", "allocation_in", "100.00")))
            .GetProperty("warnings").GetArrayLength());

        var matured = await PostAsync(app, $"{Url(id)}/switches", new
        {
            occurredOn = "2025-06-30", maturity = true,
            from = new { holdingId = fund, amount = Eur("1050.00") }, to = new { holdingId = euro, amount = Eur("1040.00") },
        });
        Assert.Equal("switch_unbalanced", Assert.Single(matured.GetProperty("warnings").EnumerateArray()).GetProperty("code").GetString());
        Assert.Equal("maturity", (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url(id)}/holdings/{fund}/movements")))
            .GetProperty("items")[1].GetProperty("kind").GetString());
        Assert.Equal("switch_unbalanced", Assert.Single((await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url(id)}/holdings")))
            .GetProperty("warnings").EnumerateArray()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_switch_takes_several_legs_on_each_side_and_reads_back_as_one_operation()
    {
        // Issue #45: one bond fund into five funds on one notice (1 000 €, 200 € each), then five into one, then two into two.
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Assurance-vie", app.AdultA, Visibility.Shared);
        async Task<Guid> Fund(string name) => (await PostAsync(app, $"{Url(id)}/holdings", new { name, kind = "fund" })).GetProperty("id").GetGuid();
        var bonds = await Fund("Obligations");
        var funds = new List<Guid>();
        for (var i = 1; i <= 5; i++)
            funds.Add(await Fund($"Fonds {i}"));
        await PostAsync(app, $"{Url(id)}/holdings/{bonds}/movements", Move("2025-01-02", "allocation_in", "5000.00", "50", "100"));

        var spread = await PostAsync(app, $"{Url(id)}/switches", new
        {
            occurredOn = "2025-01-10", label = "Investissement progressif",
            from = new[] { new { holdingId = bonds, amount = Eur("1000.00"), units = "-9.7087", unitPrice = "103.00" } },
            to = funds.Select(f => new { holdingId = f, amount = Eur("200.00") }).ToArray(),
        });
        Assert.Equal(0, spread.GetProperty("warnings").GetArrayLength());
        Assert.Equal((1, 5), (spread.GetProperty("fromMovementIds").GetArrayLength(), spread.GetProperty("toMovementIds").GetArrayLength()));

        var leg = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url(id)}/holdings/{bonds}/movements"))).GetProperty("items")
            .EnumerateArray().Single(m => m.GetProperty("kind").GetString() == "switch_out");
        var legs = leg.GetProperty("switchLegs").EnumerateArray().ToList();
        Assert.Equal(6, legs.Count);
        Assert.Equal(("switch_out", "Obligations", "-9.708700"),
            (legs[0].GetProperty("kind").GetString(), legs[0].GetProperty("holdingName").GetString(), legs[0].GetProperty("units").GetString()));
        Assert.All(legs.Skip(1), l => Assert.Equal(("switch_in", "200.0000"), (l.GetProperty("kind").GetString(), l.GetProperty("amount").GetProperty("amount").GetString())));
        var arrived = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url(id)}/holdings/{funds[2]}/movements"))).GetProperty("items")[0];
        Assert.Equal(6, arrived.GetProperty("switchLegs").GetArrayLength());

        // Five into one, 1 € short: unbalanced. Two into two within 1 €: balanced.
        var gathered = await PostAsync(app, $"{Url(id)}/switches", new
        {
            occurredOn = "2025-01-15",
            from = funds.Select(f => new { holdingId = f, amount = Eur("100.00") }).ToArray(),
            to = new[] { new { holdingId = bonds, amount = Eur("498.99") } },
        });
        Assert.Equal("switch_unbalanced", Assert.Single(gathered.GetProperty("warnings").EnumerateArray()).GetProperty("code").GetString());
        var paired = await PostAsync(app, $"{Url(id)}/switches", new
        {
            occurredOn = "2025-01-20",
            from = new[] { new { holdingId = funds[0], amount = Eur("50.00") }, new { holdingId = funds[1], amount = Eur("50.00") } },
            to = new[] { new { holdingId = funds[2], amount = Eur("60.00") }, new { holdingId = funds[3], amount = Eur("39.50") } },
        });
        Assert.Equal(0, paired.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public async Task A_switch_with_a_holding_twice_or_no_leg_on_a_side_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, euro, fund) = await HoldingsAsync(app);

        var twice = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/switches", new
        {
            occurredOn = "2025-01-10",
            from = new[] { new { holdingId = fund, amount = Eur("10.00") } },
            to = new[] { new { holdingId = euro, amount = Eur("5.00") }, new { holdingId = euro, amount = Eur("5.00") } },
        });
        var refused = await JsonAsync(twice);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "investments.invalid_movement", "to[1].holdingId"),
            (twice.StatusCode, refused.GetProperty("code").GetString(), refused.GetProperty("errors")[0].GetProperty("path").GetString()));

        var empty = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/switches",
            new { occurredOn = "2025-01-10", from = Array.Empty<object>(), to = new[] { new { holdingId = euro, amount = Eur("5.00") } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal("from", (await JsonAsync(empty)).GetProperty("errors")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Movements_out_of_their_rules_are_refused_at_their_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, euro, fund) = await HoldingsAsync(app);
        var other = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        var stranger = (await PostAsync(app, $"{Url(other)}/holdings", new { name = "ETF", kind = "etf" })).GetProperty("id").GetGuid();

        async Task Refused(string url, object body, HttpStatusCode status, string code, string? path)
        {
            var response = await app.RawAsync(app.AdultA, HttpMethod.Post, url, body);
            var json = await JsonAsync(response);
            Assert.Equal((status, code), (response.StatusCode, json.GetProperty("code").GetString()));
            if (path is not null)
                Assert.Equal(path, json.GetProperty("errors")[0].GetProperty("path").GetString());
        }

        var moves = $"{Url(id)}/holdings/{fund}/movements";
        await Refused(moves, Move("2025-01-10", "switch_in", "1.00"), HttpStatusCode.UnprocessableEntity, "investments.invalid_movement", "kind");
        await Refused(moves, Move("2025-01-10", "gift", "1.00"), HttpStatusCode.UnprocessableEntity, "investments.invalid_movement", "kind");
        await Refused(moves, Move("2025-01-10", "fee", "1.00", "0.5"), HttpStatusCode.UnprocessableEntity, "investments.invalid_movement", "units");
        await Refused(moves, Move("2025-01-10", "allocation_in", "0.00"), HttpStatusCode.UnprocessableEntity, "investments.invalid_movement", "amount");
        await Refused(moves, Move("2025-10-16", "fee", "1.00"), HttpStatusCode.UnprocessableEntity, "investments.invalid_movement", "occurredOn");
        await Refused(moves, Move("2025-01-10", "allocation_in", "1.00", flow: Guid.NewGuid()), HttpStatusCode.UnprocessableEntity,
            "investments.invalid_movement", "envelopeFlowId");
        await Refused($"{Url(id)}/holdings/{euro}/movements", Move("2025-01-10", "interest", "1.00", "1"), HttpStatusCode.UnprocessableEntity,
            "investments.invalid_movement", "units");
        await Refused($"{Url(id)}/holdings/{stranger}/movements", Move("2025-01-10", "fee", "1.00"), HttpStatusCode.NotFound, "NOT_FOUND", null);
        await Refused($"{Url(id)}/switches", new { occurredOn = "2025-01-10", from = new { holdingId = fund, amount = Eur("1") }, to = new { holdingId = fund, amount = Eur("1") } },
            HttpStatusCode.UnprocessableEntity, "investments.invalid_movement", "to[0].holdingId");
        await Refused($"{Url(id)}/switches", new { occurredOn = "2025-01-10", from = new { holdingId = fund, amount = Eur("1") }, to = new { holdingId = stranger, amount = Eur("1") } },
            HttpStatusCode.UnprocessableEntity, "investments.invalid_movement", "to[0].holdingId");
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url(id)}/holdings/{stranger}/performance?from=2025-01-01&to=2025-06-30")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url(id)}/holdings/{fund}/performance?from=2025-06-30&to=2025-01-01")).StatusCode);
    }

    [Fact]
    public async Task A_holding_with_movements_is_not_deleted_and_movements_follow_the_envelopes_access()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _, fund) = await HoldingsAsync(app);
        await PostAsync(app, $"{Url(id)}/holdings/{fund}/movements", Move("2025-01-10", "fee", "1.00"));
        var version = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url(id)}/holdings"))).GetProperty("holdings")
            .EnumerateArray().Single(h => h.GetProperty("id").GetGuid() == fund).GetProperty("version").GetUInt32();

        var deleted = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Url(id)}/holdings/{fund}?version={version}");
        Assert.Equal((HttpStatusCode.Conflict, "investments.holding_in_use"), (deleted.StatusCode, (await JsonAsync(deleted)).GetProperty("code").GetString()));

        var hidden = await app.NewEnvelopeAsync("Secret", app.AdultA, Visibility.Private);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Url(hidden)}/holdings/{fund}/movements")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Url(id)}/holdings/{fund}/movements")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await app.RawAsync(app.AdultB, HttpMethod.Post, $"{Url(id)}/holdings/{fund}/movements", Move("2025-01-10", "fee", "1.00"))).StatusCode);

        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/close", new { closedOn = "2025-09-30" })).EnsureSuccessStatusCode();
        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holdings/{fund}/movements", Move("2025-01-10", "fee", "1.00"));
        Assert.Equal((HttpStatusCode.Conflict, "investments.envelope_closed"), (refused.StatusCode, (await JsonAsync(refused)).GetProperty("code").GetString()));
    }
}
