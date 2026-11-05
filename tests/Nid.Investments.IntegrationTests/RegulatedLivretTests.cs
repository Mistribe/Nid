using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.Investments.IntegrationTests.RoutineTests;

namespace Nid.Investments.IntegrationTests;

/// <summary>Issues #399–#401 through the API; today is 2025-10-15 (2025: 3 % in January, 2,4 % from February, 1,7 % from August).</summary>
public class RegulatedLivretTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<ItemId> CreateAsync(NidApp app, string name, string kind, string? regulated = null, string? openedOn = null)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = name, kind, currency = "EUR", visibility = "shared", regulated, openedOn,
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return new ItemId(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid());
    }

    private static async Task<JsonElement> DetailAsync(NidApp app, ItemId id)
        => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/investments/envelopes/{id.Value}")).RootElement;

    [Fact]
    public async Task A_livret_shows_its_ceiling_and_the_year_s_interest_as_an_estimate()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var full = await CreateAsync(app, "Livret A", "savings", "livret_a");
        await ValueAsync(app, full, "2025-01-02", "23104.60"); // over through interest: not an error
        var room = await CreateAsync(app, "LDDS", "savings", "ldds");
        await ValueAsync(app, room, "2025-01-02", "10000.00");
        var plain = await CreateAsync(app, "Compte", "savings");

        var detail = await DetailAsync(app, full);
        Assert.Equal("livret_a", detail.GetProperty("regulated").GetString());
        Assert.Equal(22_950m, Dec(detail.GetProperty("ceiling").GetProperty("ceiling")));
        Assert.True(detail.GetProperty("ceiling").GetProperty("reached").GetBoolean());
        Assert.Equal(23_104.60m, Dec(detail.GetProperty("figures").GetProperty("value"))); // the value stays the recorded one

        var ldds = await DetailAsync(app, room);
        Assert.Equal(2_000m, Dec(ldds.GetProperty("ceiling").GetProperty("room")));
        // 10 000 × (2 × 3 + 12 × 2,4 + 10 × 1,7) / 24 %, and to 30 September (… + 4 × 1,7).
        Assert.Equal(215.83m, Dec(ldds.GetProperty("interest").GetProperty("expected")));
        Assert.Equal(173.33m, Dec(ldds.GetProperty("interest").GetProperty("toDate")));

        var none = await DetailAsync(app, plain);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("ceiling").ValueKind);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("interest").ValueKind);

        var watch = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/investments/overview")).RootElement.GetProperty("watch")
            .EnumerateArray().Select(w => $"{w.GetProperty("code").GetString()} {w.GetProperty("envelopeId").GetGuid() == full.Value}").ToList();
        Assert.Contains("ceiling_reached True", watch);
        Assert.Contains("ceiling_room False", watch);
    }

    [Fact]
    public async Task Only_a_savings_envelope_is_a_regulated_livret_and_leaving_savings_drops_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = "PEA", kind = "pea", currency = "EUR", regulated = "livret_a",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var livret = await CreateAsync(app, "Livret", "savings");
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"/api/v1/investments/envelopes/{livret.Value}", new { regulated = "lep" })).EnsureSuccessStatusCode();
        Assert.Equal("lep", (await DetailAsync(app, livret)).GetProperty("regulated").GetString());
        // Set and cleared at once is a mistake, refused, never a silent clear.
        var both = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"/api/v1/investments/envelopes/{livret.Value}", new { regulated = "ldds", clearRegulated = true });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, both.StatusCode);
        var refusal = await both.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("investments.invalid_envelope", refusal);
        Assert.Contains("\"regulated\"", refusal);
        Assert.Equal("lep", (await DetailAsync(app, livret)).GetProperty("regulated").GetString());
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"/api/v1/investments/envelopes/{livret.Value}", new { kind = "checking" })).EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Null, (await DetailAsync(app, livret)).GetProperty("regulated").ValueKind);
    }

    [Fact]
    public async Task The_routine_refuses_a_deposit_beyond_the_ceiling()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var lep = await CreateAsync(app, "LEP", "savings", "lep");
        await ValueAsync(app, lep, "2025-08-31", "9900.00");

        var line = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/investments/routine?on=2025-09-30")).RootElement
            .GetProperty("lines")[0];
        Assert.Equal(100m, Dec(line.GetProperty("ceilingRoom")));

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/routine", new
        {
            on = "2025-09-30",
            lines = new[] { new { envelopeId = lep.Value, value = (object?)null, contribution = new { amount = "150.00", currency = "EUR" } } },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var body = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("investments.above_ceiling", body);
        Assert.Contains("lines[0].contribution", body);

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/routine", new
        {
            on = "2025-09-30",
            lines = new[] { new { envelopeId = lep.Value, value = (object?)null, contribution = new { amount = "100.00", currency = "EUR" } } },
        })).StatusCode);
    }

    [Fact]
    public async Task The_envelope_page_and_a_transfer_refuse_a_deposit_beyond_the_ceiling_too()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var lep = await CreateAsync(app, "LEP", "savings", "lep");
        await ValueAsync(app, lep, "2025-08-31", "9900.00");
        var flows = $"/api/v1/investments/envelopes/{lep.Value}/flows";
        object Flow(string kind, string amount) => new { occurredOn = "2025-09-30", kind, amount = new { amount, currency = "EUR" } };
        async Task RefusedAsync(HttpResponseMessage r, string field)
        {
            var body = await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Contains("investments.above_ceiling", body);
            Assert.Contains($"\"{field}\"", body);
        }

        await RefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, flows, Flow("contribution", "150.00")), "amount");
        var paid = await app.RawAsync(app.AdultA, HttpMethod.Post, flows, Flow("contribution", "60.00"));
        Assert.Equal(HttpStatusCode.Created, paid.StatusCode);
        var flowId = JsonDocument.Parse(await paid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("id").GetGuid();

        // A correction is read without the flow it replaces: 100 fits, 101 does not.
        await RefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{flows}/{flowId}", Flow("contribution", "101.00")), "amount");
        var corrected = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{flows}/{flowId}", Flow("contribution", "100.00"));
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);

        // A withdrawal is never refused; a transfer into the full livret is.
        Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post, flows, Flow("withdrawal", "20000.00"))).StatusCode);
        var full = await CreateAsync(app, "Livret A", "savings", "livret_a");
        await ValueAsync(app, full, "2025-08-31", "23104.60"); // over through interest: not an error
        var checking = await CreateAsync(app, "Compte", "checking");
        await RefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/transfers",
            new { from = checking.Value, to = full.Value, occurredOn = "2025-09-30", amount = new { amount = "0.01", currency = "EUR" } }), "amount");
    }

    [Fact]
    public async Task A_life_insurance_s_8_years_are_on_the_detail_in_a_venir_s_window_and_watched_90_days_ahead()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var soon = await CreateAsync(app, "Vita", "life_insurance", openedOn: "2017-12-01"); // 8 years on 2025-12-01: 47 days
        var watched = await CreateAsync(app, "Vesta", "life_insurance", openedOn: "2017-12-30"); // 2025-12-30: 76 days, beyond the dashboard's 60
        var later = await CreateAsync(app, "Lointaine", "life_insurance", openedOn: "2018-03-01"); // 2026-03-01: 137 days

        var milestone = (await DetailAsync(app, soon)).GetProperty("milestone");
        Assert.Equal("life_insurance_8y", milestone.GetProperty("code").GetString());
        Assert.Equal("2025-12-01", milestone.GetProperty("on").GetString());
        Assert.Equal("2026-03-01", (await DetailAsync(app, later)).GetProperty("milestone").GetProperty("on").GetString());

        var events = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/dashboard?scope=household")).RootElement
            .GetProperty("upcoming").EnumerateArray().Where(e => e.GetProperty("kind").GetString() == "tax_milestone")
            .Select(e => $"{e.GetProperty("on").GetString()} {e.GetProperty("name").GetString()} {e.GetProperty("label").GetString()}");
        Assert.Equal(["2025-12-01 Vita life_insurance_8y"], events);

        var watch = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/investments/overview")).RootElement.GetProperty("watch")
            .EnumerateArray().Where(w => w.GetProperty("code").GetString() == "tax_milestone").Select(w => w.GetProperty("envelopeId").GetGuid());
        Assert.Equal(new[] { soon.Value, watched.Value }.Order(), watch.Order());
    }
}
