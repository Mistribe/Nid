using System.Globalization;
using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Investments;

public class RoutineTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Url = "/api/v1/investments/routine";

    internal static async Task<ItemId> EnvelopeAsync(NidApp app, string name, string cadence, PersonId? owner = null, string visibility = "shared")
    {
        var who = owner ?? app.AdultA;
        var response = await app.RawAsync(who, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = name, kind = "savings", currency = "EUR", visibility, valuationCadence = cadence,
            owners = new[] { new { personId = who.Value, shareBp = 10_000 } },
        });
        response.EnsureSuccessStatusCode();
        return new ItemId(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid());
    }

    internal static async Task ValueAsync(NidApp app, ItemId envelope, string asOf, string amount, PersonId? writer = null)
        => Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(writer ?? app.AdultA, HttpMethod.Post,
            $"/api/v1/investments/envelopes/{envelope.Value}/valuations",
            new { asOf, amount = new { amount, currency = "EUR" }, note = (string?)null })).StatusCode);

    internal static decimal Dec(JsonElement money) => decimal.Parse(money.GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);

    private static async Task<JsonElement> ListAsync(NidApp app, PersonId reader, string query = "")
    {
        var response = await app.RawAsync(reader, HttpMethod.Get, Url + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task The_list_defaults_to_the_end_of_last_month_and_puts_due_envelopes_first()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var done = await EnvelopeAsync(app, "À jour", "monthly");
        await ValueAsync(app, done, "2025-09-30", "500.00");
        var due = await EnvelopeAsync(app, "Zèbre", "monthly");
        await ValueAsync(app, due, "2025-08-31", "1000.00");
        await EnvelopeAsync(app, "Manuel", "manual");

        var view = await ListAsync(app, app.AdultA);
        var lines = view.GetProperty("lines").EnumerateArray().ToList();

        Assert.Equal("2025-09-30", view.GetProperty("on").GetString());
        Assert.Equal(new[] { "Zèbre", "À jour", "Manuel" }, lines.Select(l => l.GetProperty("name").GetString()));
        Assert.True(lines[0].GetProperty("due").GetBoolean());
        Assert.Equal(1000m, Dec(lines[0].GetProperty("lastValue")));
        Assert.Equal("2025-08-31", lines[0].GetProperty("lastAsOf").GetString());
        Assert.False(lines[1].GetProperty("due").GetBoolean());
        Assert.False(lines[2].GetProperty("due").GetBoolean());
        Assert.Equal("manual", lines[2].GetProperty("cadence").GetString());
        Assert.Equal(JsonValueKind.Null, lines[2].GetProperty("lastValue").ValueKind);
    }

    [Fact]
    public async Task The_list_takes_a_date_and_refuses_one_in_the_future()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var quarterly = await EnvelopeAsync(app, "Trimestriel", "quarterly");
        await ValueAsync(app, quarterly, "2025-07-10", "100.00");

        Assert.False((await ListAsync(app, app.AdultA, "?on=2025-09-30")).GetProperty("lines")[0].GetProperty("due").GetBoolean());
        var future = await app.RawAsync(app.AdultA, HttpMethod.Get, Url + "?on=2025-10-31");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, future.StatusCode);
        Assert.Contains("core.date_in_future", await future.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_list_holds_only_open_envelopes_the_reader_may_write()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await EnvelopeAsync(app, "Secret", "monthly", app.AdultB, "private");
        var closed = await EnvelopeAsync(app, "Clos", "monthly");
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{closed.Value}/close", new { closedOn = "2025-06-01" }))
            .EnsureSuccessStatusCode();
        await EnvelopeAsync(app, "Mien", "monthly");

        var names = (await ListAsync(app, app.AdultA)).GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("name").GetString());

        Assert.Equal(new[] { "Mien" }, names);
    }

    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task<HttpResponseMessage> RecordAsync(NidApp app, PersonId writer, object body)
        => await app.RawAsync(writer, HttpMethod.Post, Url, body);

    private static async Task<JsonElement> LatestAsync(NidApp app, ItemId envelope)
        => (await ListAsync(app, app.AdultA, "?on=2025-10-15")).GetProperty("lines").EnumerateArray()
            .Single(l => l.GetProperty("id").GetGuid() == envelope.Value);

    [Fact]
    public async Task The_recap_gives_the_gain_of_the_month_net_of_contributions()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var pea = await EnvelopeAsync(app, "PEA", "monthly");
        await ValueAsync(app, pea, "2025-08-31", "1000.00");

        var response = await RecordAsync(app, app.AdultA, new
        {
            on = "2025-09-30",
            lines = new[] { new { envelopeId = pea.Value, value = Eur("1100.00"), contribution = Eur("50.00") } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var line = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("lines")[0];
        Assert.Equal(1000m, Dec(line.GetProperty("previousValue")));
        Assert.Equal("2025-08-31", line.GetProperty("previousAsOf").GetString());
        Assert.Equal(1100m, Dec(line.GetProperty("value")));
        Assert.Equal(50m, Dec(line.GetProperty("contributions")));
        Assert.Equal(50m, Dec(line.GetProperty("gain")));
        Assert.Equal("PEA", line.GetProperty("name").GetString());
        Assert.NotEqual(JsonValueKind.Null, line.GetProperty("valuationId").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, line.GetProperty("flowId").ValueKind);
        Assert.Equal(1100m, Dec((await LatestAsync(app, pea)).GetProperty("lastValue")));
    }

    [Fact]
    public async Task A_first_value_has_no_gain_and_a_contribution_alone_records_only_a_flow()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var fresh = await EnvelopeAsync(app, "Neuf", "monthly");
        var paid = await EnvelopeAsync(app, "Versement", "monthly");
        await ValueAsync(app, paid, "2025-08-31", "200.00");

        var response = await RecordAsync(app, app.AdultA, new
        {
            on = "2025-09-30",
            lines = new object[]
            {
                new { envelopeId = fresh.Value, value = Eur("300.00"), contribution = (object?)null },
                new { envelopeId = paid.Value, value = (object?)null, contribution = Eur("25.00") },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("lines");
        Assert.Equal(JsonValueKind.Null, lines[0].GetProperty("gain").ValueKind);
        Assert.Equal(JsonValueKind.Null, lines[0].GetProperty("previousValue").ValueKind);
        Assert.Equal(JsonValueKind.Null, lines[1].GetProperty("valuationId").ValueKind);
        Assert.Equal(JsonValueKind.Null, lines[1].GetProperty("gain").ValueKind);
        Assert.Equal(25m, Dec(lines[1].GetProperty("contributions")));
    }

    [Fact]
    public async Task One_line_the_reader_may_not_write_refuses_the_whole_routine_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await EnvelopeAsync(app, "Mien", "monthly");
        var secret = await EnvelopeAsync(app, "Secret", "monthly", app.AdultB, "private");

        var response = await RecordAsync(app, app.AdultA, new
        {
            on = "2025-09-30",
            lines = new[]
            {
                new { envelopeId = mine.Value, value = Eur("10.00") },
                new { envelopeId = secret.Value, value = Eur("20.00") },
            },
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await LatestAsync(app, mine)).GetProperty("lastValue").ValueKind);
    }

    [Theory]
    [InlineData("negative", "lines[1].value", "investments.routine_invalid_amount")]
    [InlineData("zero_contribution", "lines[1].contribution", "investments.routine_invalid_amount")]
    [InlineData("empty", "lines[1]", "investments.routine_empty_line")]
    [InlineData("duplicate", "lines[1]", "investments.routine_duplicate")]
    public async Task A_bad_line_is_named_and_nothing_is_written(string kind, string path, string code)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var first = await EnvelopeAsync(app, "Premier", "monthly");
        var second = await EnvelopeAsync(app, "Second", "monthly");
        object bad = kind switch
        {
            "negative" => (object)new { envelopeId = second.Value, value = Eur("-1.00"), contribution = (object?)null },
            "zero_contribution" => (object)new { envelopeId = second.Value, value = (object?)null, contribution = Eur("0") },
            "empty" => (object)new { envelopeId = second.Value, value = (object?)null, contribution = (object?)null },
            _ => (object)new { envelopeId = first.Value, value = Eur("5.00"), contribution = (object?)null },
        };

        var response = await RecordAsync(app, app.AdultA, new
        {
            on = "2025-09-30",
            lines = new object[] { new { envelopeId = first.Value, value = Eur("10.00"), contribution = (object?)null }, bad },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(path, problem.GetProperty("errors")[0].GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.Null, (await LatestAsync(app, first)).GetProperty("lastValue").ValueKind);
    }

    [Fact]
    public async Task A_routine_dated_in_the_future_is_refused_at_its_date()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var pea = await EnvelopeAsync(app, "PEA", "monthly");
        var response = await RecordAsync(app, app.AdultA, new { on = "2025-10-31", lines = new[] { new { envelopeId = pea.Value, value = Eur("1.00") } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("\"path\":\"on\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Undo_voids_everything_the_routine_recorded()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var pea = await EnvelopeAsync(app, "PEA", "monthly");
        await ValueAsync(app, pea, "2025-08-31", "1000.00");
        var recorded = JsonDocument.Parse(await (await RecordAsync(app, app.AdultA, new
        {
            on = "2025-09-30",
            lines = new[] { new { envelopeId = pea.Value, value = Eur("1100.00"), contribution = Eur("50.00") } },
        })).Content.ReadAsStringAsync()).RootElement.GetProperty("lines")[0];

        var undo = await app.RawAsync(app.AdultA, HttpMethod.Post, Url + "/undo", new
        {
            facts = new[] { new { envelopeId = pea.Value, valuationId = recorded.GetProperty("valuationId").GetGuid(), flowId = recorded.GetProperty("flowId").GetGuid() } },
        });

        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Equal(2, JsonDocument.Parse(await undo.Content.ReadAsStringAsync()).RootElement.GetProperty("voided").GetInt32());
        var line = await LatestAsync(app, pea);
        Assert.Equal(1000m, Dec(line.GetProperty("lastValue")));
        Assert.Equal("2025-08-31", line.GetProperty("lastAsOf").GetString());
        var again = await app.RawAsync(app.AdultA, HttpMethod.Post, Url + "/undo", new
        {
            facts = new[] { new { envelopeId = pea.Value, valuationId = recorded.GetProperty("valuationId").GetGuid(), flowId = (Guid?)null } },
        });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode); // already retired
    }

    [Fact]
    public async Task A_value_at_a_date_that_already_has_one_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var pea = await EnvelopeAsync(app, "PEA", "monthly");
        await ValueAsync(app, pea, "2025-09-30", "1000.00");

        var response = await RecordAsync(app, app.AdultA, new { on = "2025-09-30", lines = new[] { new { envelopeId = pea.Value, value = Eur("1100.00") } } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("investments.routine_value_exists", problem.GetProperty("code").GetString());
        Assert.Equal("lines[0].value", problem.GetProperty("errors")[0].GetProperty("path").GetString());
        Assert.Equal(1000m, Dec((await LatestAsync(app, pea)).GetProperty("lastValue")));
    }

    [Fact]
    public async Task Undo_refuses_an_income_sources_flow_and_a_closed_envelopes_facts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/income/sources", new Dictionary<string, object?>
        {
            ["displayName"] = "Salaire", ["kind"] = "salary", ["currency"] = "EUR", ["visibility"] = "shared",
            ["owners"] = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            ["phases"] = new[] { new { label = "Salaire", startsOn = "2025-01-01", endsOn = (string?)null, netAmount = Eur("2000"), every = new { count = 1, unit = "month" }, maxCycles = (int?)null } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var source = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var oneOff = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/income/sources/{source}/one-offs", new { occurredOn = "2025-06-10", amount = Eur("500"), note = "Prime" });
        var flow = JsonDocument.Parse(await oneOff.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var undo = await app.RawAsync(app.AdultA, HttpMethod.Post, Url + "/undo", new { facts = new[] { new { envelopeId = source, valuationId = (Guid?)null, flowId = (Guid?)flow } } });
        Assert.Equal(HttpStatusCode.NotFound, undo.StatusCode);
        var detail = await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/income/sources/{source}");
        Assert.Contains(flow.ToString(), await detail.Content.ReadAsStringAsync());

        var pea = await EnvelopeAsync(app, "PEA", "monthly");
        var recorded = JsonDocument.Parse(await (await RecordAsync(app, app.AdultA, new { on = "2025-09-30", lines = new[] { new { envelopeId = pea.Value, value = Eur("10.00") } } })).Content.ReadAsStringAsync()).RootElement.GetProperty("lines")[0];
        var close = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{pea.Value}/close", new { closedOn = "2025-09-30", finalWithdrawal = (object?)null });
        Assert.True(close.IsSuccessStatusCode);
        var closedUndo = await app.RawAsync(app.AdultA, HttpMethod.Post, Url + "/undo", new { facts = new[] { new { envelopeId = pea.Value, valuationId = recorded.GetProperty("valuationId").GetGuid(), flowId = (Guid?)null } } });
        Assert.Equal(HttpStatusCode.NotFound, closedUndo.StatusCode);
    }

    [Fact]
    public async Task A_partial_undo_is_all_or_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var pea = await EnvelopeAsync(app, "PEA", "monthly");
        var recorded = JsonDocument.Parse(await (await RecordAsync(app, app.AdultA, new
        {
            on = "2025-09-30", lines = new[] { new { envelopeId = pea.Value, value = Eur("1100.00"), contribution = Eur("50.00") } },
        })).Content.ReadAsStringAsync()).RootElement.GetProperty("lines")[0];
        var valuation = recorded.GetProperty("valuationId").GetGuid();
        var flow = recorded.GetProperty("flowId").GetGuid();
        var delete = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"/api/v1/investments/envelopes/{pea.Value}/flows/{flow}");
        Assert.True(delete.IsSuccessStatusCode);

        var undo = await app.RawAsync(app.AdultA, HttpMethod.Post, Url + "/undo", new { facts = new[] { new { envelopeId = pea.Value, valuationId = (Guid?)valuation, flowId = (Guid?)flow } } });

        Assert.Equal(HttpStatusCode.Conflict, undo.StatusCode);
        Assert.Equal(1100m, Dec((await LatestAsync(app, pea)).GetProperty("lastValue")));
    }
}
