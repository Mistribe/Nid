using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Dashboard;

[Collection("postgres")]
public class SplitTests(PostgresFixture fixture)
{
    private static async Task ValueAsync(NidApp app, PersonId writer, ItemId envelope, string asOf, string amount, string currency = "EUR")
        => Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(writer, HttpMethod.Post,
            $"/api/v1/investments/envelopes/{envelope.Value}/valuations",
            new { asOf, amount = new { amount, currency }, note = (string?)null })).StatusCode);

    private static async Task<JsonElement> DashboardAsync(NidApp app, PersonId reader, string scope = "household")
        => JsonDocument.Parse(await app.GetRawAsync(reader, $"/api/v1/dashboard?scope={scope}")).RootElement;

    private static string? Amount(JsonElement money) => money.ValueKind == JsonValueKind.Null ? null : money.GetProperty("amount").GetString();

    [Fact]
    public async Task The_envelope_split_sorts_by_value_then_lists_envelopes_without_value()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var small = await app.NewEnvelopeAsync("Petit", app.AdultA, Visibility.Shared);
        var big = await app.NewEnvelopeAsync("Grand", app.AdultA, Visibility.Shared);
        await app.NewEnvelopeAsync("Zèbre", app.AdultA, Visibility.Shared);
        await app.NewEnvelopeAsync("Vide", app.AdultA, Visibility.Shared);
        await app.NewEnvelopeAsync("épargne", app.AdultA, Visibility.Shared);
        await ValueAsync(app, app.AdultA, small, "2025-08-01", "100.00"); // monthly cadence: stale on 2025-10-15
        await ValueAsync(app, app.AdultA, big, "2025-10-01", "900.00");

        var rows = (await DashboardAsync(app, app.AdultA)).GetProperty("envelopeSplit").EnumerateArray().ToList();

        Assert.Equal(new[] { "Grand", "Petit", "épargne", "Vide", "Zèbre" }, rows.Select(r => r.GetProperty("name").GetString()));
        Assert.All(rows, r => Assert.Equal("investments", r.GetProperty("module").GetString()));
        Assert.Equal("900.00", Amount(rows[0].GetProperty("converted")));
        Assert.Equal("2025-10-01", rows[0].GetProperty("asOf").GetString());
        Assert.False(rows[0].GetProperty("stale").GetBoolean());
        Assert.True(rows[1].GetProperty("stale").GetBoolean());
        Assert.Null(Amount(rows[2].GetProperty("value")));
        Assert.Null(Amount(rows[2].GetProperty("converted")));
        Assert.Equal(JsonValueKind.Null, rows[2].GetProperty("missingCurrency").ValueKind);
    }

    [Fact]
    public async Task A_jointly_owned_envelope_splits_pro_rata_and_a_dependents_counts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var joint = await app.NewEnvelopeAsync("Commun", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        var child = await app.NewEnvelopeAsync("Livret enfant", Visibility.Shared, (app.Dependent, 10_000));
        await ValueAsync(app, app.AdultA, joint, "2025-10-01", "1000.00");
        await ValueAsync(app, app.AdultA, child, "2025-10-01", "300.00");

        var people = (await DashboardAsync(app, app.AdultA)).GetProperty("people").EnumerateArray().ToList();

        Assert.Equal(3, people.Count);
        var byId = people.ToDictionary(p => p.GetProperty("id").GetGuid());
        Assert.Equal("500.00", Amount(byId[app.AdultA.Value].GetProperty("amount")));
        Assert.Equal("500.00", Amount(byId[app.AdultB.Value].GetProperty("amount")));
        Assert.Equal("300.00", Amount(byId[app.Dependent.Value].GetProperty("amount")));
        Assert.Equal(38.5m, byId[app.AdultA.Value].GetProperty("pct").GetDecimal()); // 500 ÷ 1300
        Assert.Equal(23.1m, byId[app.Dependent.Value].GetProperty("pct").GetDecimal());
        Assert.Equal(app.Dependent.Value, people[2].GetProperty("id").GetGuid()); // sorted by amount
        // Adults take the first colour slots, the dependent the next one.
        Assert.True(byId[app.Dependent.Value].GetProperty("slot").GetInt32() > byId[app.AdultA.Value].GetProperty("slot").GetInt32());
        Assert.True(byId[app.Dependent.Value].GetProperty("slot").GetInt32() > byId[app.AdultB.Value].GetProperty("slot").GetInt32());
        Assert.Equal(JsonValueKind.Null, (await DashboardAsync(app, app.AdultA)).GetProperty("peopleMissingCurrency").ValueKind);
    }

    [Fact]
    public async Task A_missing_rate_makes_the_person_split_unknown_and_keeps_the_envelopes_own_amount()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var dollars = await app.NewEnvelopeAsync("Dollars", app.AdultA, Visibility.Shared, "USD");
        await ValueAsync(app, app.AdultA, dollars, "2025-10-01", "100.00", "USD"); // the test database holds no rate

        var view = await DashboardAsync(app, app.AdultA);
        var row = view.GetProperty("envelopeSplit")[0];

        Assert.Equal(JsonValueKind.Null, view.GetProperty("people").ValueKind);
        Assert.Equal("USD", view.GetProperty("peopleMissingCurrency").GetString());
        Assert.Equal("100.00", Amount(row.GetProperty("value")));
        Assert.Equal("USD", row.GetProperty("value").GetProperty("currency").GetString());
        Assert.Null(Amount(row.GetProperty("converted")));
        Assert.Equal("USD", row.GetProperty("missingCurrency").GetString());
    }

    [Fact]
    public async Task A_zero_value_is_a_value_and_gives_no_person_share()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var empty = await app.NewEnvelopeAsync("Soldé", app.AdultA, Visibility.Shared);
        await ValueAsync(app, app.AdultA, empty, "2025-10-01", "0.00");

        var view = await DashboardAsync(app, app.AdultA);

        Assert.Equal("0.00", Amount(view.GetProperty("envelopeSplit")[0].GetProperty("converted")));
        Assert.Equal(0, view.GetProperty("envelopesWithoutValue").GetInt32());
        Assert.Equal(0, view.GetProperty("people").GetArrayLength());
    }

    [Theory]
    [InlineData("household")]
    [InlineData("me")]
    public async Task The_other_adults_private_envelope_is_in_no_split(string scope)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var secret = await app.NewEnvelopeAsync("Secret", app.AdultB, Visibility.Private);
        await ValueAsync(app, app.AdultB, secret, "2025-10-01", "5000.00");

        var view = await DashboardAsync(app, app.AdultA, scope);

        Assert.Equal(0, view.GetProperty("envelopeSplit").GetArrayLength());
        Assert.Equal(0, view.GetProperty("people").GetArrayLength());
    }
}
