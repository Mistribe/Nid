using System.Globalization;
using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Investments;

[Collection("postgres")]
public class RoutineTests(PostgresFixture fixture)
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
}
