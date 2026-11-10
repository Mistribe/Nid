using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application.Dashboard;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using NodaTime.Text;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.Core.IntegrationTests.Dashboard;

/// <summary>3E1 spec §2: properties and loans in the net worth, its split, its change since 1 January and its curve.</summary>
public class RealEstateNetWorthTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<Guid> EstimateAsync(NidApp app, Guid property, string asOf, string amount)
        => await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/valuations", new { asOf, amount }));

    /// <summary><see cref="Short"/> (12 000 at 0 %, twelve payments of 1 000), disbursed and first paid on the given dates.</summary>
    private static Dictionary<string, object?> Loan(NidApp app, string name, string disbursedOn, string firstPaymentDate)
    {
        var body = Short(app, name);
        body["disbursedOn"] = disbursedOn;
        body["firstPaymentDate"] = firstPaymentDate;
        return body;
    }

    private static async Task<JsonElement> DashboardAsync(NidApp app, PersonId reader, string scope = "household")
        => JsonDocument.Parse(await app.GetRawAsync(reader, $"/api/v1/dashboard?scope={scope}")).RootElement;

    private static string? Total(JsonElement figure)
        => figure.GetProperty("total").ValueKind == JsonValueKind.Null ? null : figure.GetProperty("total").GetProperty("amount").GetString();

    private static string? Amount(JsonElement money) => money.ValueKind == JsonValueKind.Null ? null : money.GetProperty("amount").GetString();

    /// <summary>The module's own curve points on the given dates, read by A over the household.</summary>
    private static async Task<IReadOnlyList<NetWorthPoint>> HistoryAsync(NidApp app, params string[] dates)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
        var source = scope.ServiceProvider.GetServices<INetWorthContribution>().Single(c => c.Module == "real_estate");
        var history = await source.NetWorthHistoryAsync(app.AdultA, VisibilityScope.Household,
            dates.Select(d => LocalDatePattern.Iso.Parse(d).Value).ToList(), TestContext.Current.CancellationToken);
        Assert.True(history.TryGetValue(out var points));
        return points!;
    }

    /// <summary>Archives an item on a day, mid-morning UTC: the same day in the household's zone.</summary>
    private static async Task ArchiveAsync(NidApp app, Guid id, int year, int month, int day)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        Assert.True((await scope.ServiceProvider.GetRequiredService<IItemRegistry>()
            .ArchiveAsync(new ItemId(id), Instant.FromUtc(year, month, day, 10, 0), TestContext.Current.CancellationToken)).IsSuccess);
    }

    private static decimal Eur(IReadOnlyDictionary<Currency, decimal>? sums) => sums?.GetValueOrDefault(Currency.Eur) ?? 0m;

    /// <summary>Each net worth part's change between the two points, by key.</summary>
    private static Dictionary<string, decimal> Parts(NetWorthPoint before, NetWorthPoint now)
        => MonthChanges.NetWorthParts("real_estate", before, now).ToDictionary(p => p.Key, p => Eur(p.Now) - Eur(p.Before));

    [Fact]
    public async Task A_property_and_its_loan_are_two_lines_the_loan_negative_each_split_by_its_own_shares()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app, "Maison", "shared", app.AdultA, app.AdultB)); // 50 / 50
        await EstimateAsync(app, home, "2024-12-31", "300000");
        await EstimateAsync(app, home, "2025-09-30", "310000");
        var land = await NewPropertyAsync(app, Home(app, "Terrain")); // never estimated
        await NewAsync(app, Short(app, "Prêt travaux")); // A alone; ten payments of 1 000 made by 2025-10-15: 2 000 owed

        var view = await DashboardAsync(app, app.AdultA);

        Assert.Equal("308000.00", Total(view.GetProperty("netWorth")));
        Assert.Equal(3, view.GetProperty("envelopes").GetInt32());
        Assert.Equal(1, view.GetProperty("envelopesWithoutValue").GetInt32());
        Assert.Equal(0, view.GetProperty("staleEnvelopes").GetInt32());

        var rows = view.GetProperty("envelopeSplit").EnumerateArray().ToList();
        Assert.Equal(new[] { "Maison", "Prêt travaux", "Terrain" }, rows.Select(r => r.GetProperty("name").GetString()));
        Assert.Equal(new[] { "properties", "loans", "properties" }, rows.Select(r => r.GetProperty("module").GetString()));
        Assert.Equal("310000.00", Amount(rows[0].GetProperty("converted")));
        Assert.Equal("2025-09-30", rows[0].GetProperty("asOf").GetString());
        Assert.Equal("-2000.00", Amount(rows[1].GetProperty("value")));
        Assert.Equal("-2000.00", Amount(rows[1].GetProperty("converted")));
        Assert.Equal("2025-10-15", rows[1].GetProperty("asOf").GetString());
        Assert.False(rows[1].GetProperty("stale").GetBoolean());
        Assert.Null(Amount(rows[2].GetProperty("value")));

        // README §8: no netting — A holds half the property and the whole loan, B half the property.
        var people = view.GetProperty("people").EnumerateArray().ToDictionary(p => p.GetProperty("id").GetGuid());
        Assert.Equal("153000.00", Amount(people[app.AdultA.Value].GetProperty("amount")));
        Assert.Equal("155000.00", Amount(people[app.AdultB.Value].GetProperty("amount")));
        Assert.Equal(49.7m, people[app.AdultA.Value].GetProperty("pct").GetDecimal());

        // The estimate's 10 000 is a gain; the loan's 10 000 of principal repaid since 1 January is paid in, its gain 0.
        Assert.Equal("10000.00", Total(view.GetProperty("change")));
        Assert.Equal("10000.00", Total(view.GetProperty("changeContributions")));
        Assert.Equal(3.4m, view.GetProperty("changePct").GetDecimal()); // 10 000 ÷ (300 000 − 12 000 + 10 000)
        Assert.Equal(0, view.GetProperty("envelopesWithoutStart").GetInt32());

        var todo = Assert.Single(view.GetProperty("todo").EnumerateArray());
        Assert.Equal(("no_value", "properties", land), (todo.GetProperty("kind").GetString(), todo.GetProperty("module").GetString(), todo.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task A_private_loan_on_a_shared_property_is_deducted_only_in_Moi()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app));
        await EstimateAsync(app, home, "2025-09-30", "310000");
        var body = Short(app, "Prêt perso");
        body["visibility"] = "private";
        body["propertyId"] = home;
        var loan = await NewAsync(app, body);

        var household = await DashboardAsync(app, app.AdultA, "household");
        var me = await DashboardAsync(app, app.AdultA, "me");
        var bob = await DashboardAsync(app, app.AdultB, "me");

        Assert.Equal("310000.00", Total(household.GetProperty("netWorth"))); // README §8 visibility_mismatch: not deducted
        Assert.DoesNotContain(household.GetProperty("envelopeSplit").EnumerateArray(), r => r.GetProperty("id").GetGuid() == loan);
        Assert.Equal("308000.00", Total(me.GetProperty("netWorth")));
        Assert.Contains(me.GetProperty("envelopeSplit").EnumerateArray(), r => r.GetProperty("id").GetGuid() == loan);
        Assert.Equal("310000.00", Total(bob.GetProperty("netWorth")));
        Assert.DoesNotContain(bob.GetProperty("envelopeSplit").EnumerateArray(), r => r.GetProperty("id").GetGuid() == loan);
    }

    [Fact]
    public async Task A_stale_estimate_and_a_loan_of_this_year_stay_out_of_the_change_and_an_ended_loan_out_of_the_split()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var old = await NewPropertyAsync(app, Home(app, "Ancienne"));
        await EstimateAsync(app, old, "2023-06-01", "250000"); // yearly: more than 400 days old, today and on 1 January alike
        await NewAsync(app, Loan(app, "Prêt récent", "2025-02-01", "2025-02-15")); // nine payments made: 3 000 owed; nothing on 1 January
        var ended = await NewAsync(app, Loan(app, "Prêt soldé", "2024-01-01", "2024-01-15")); // last payment 2024-12-15

        var view = await DashboardAsync(app, app.AdultA);

        Assert.Equal("247000.00", Total(view.GetProperty("netWorth")));
        Assert.Equal(2, view.GetProperty("envelopes").GetInt32());
        Assert.Equal(1, view.GetProperty("staleEnvelopes").GetInt32());
        Assert.Equal(2, view.GetProperty("envelopesWithoutStart").GetInt32());
        Assert.Null(Total(view.GetProperty("change")));
        Assert.DoesNotContain(view.GetProperty("envelopeSplit").EnumerateArray(), r => r.GetProperty("id").GetGuid() == ended);
        var todo = Assert.Single(view.GetProperty("todo").EnumerateArray());
        Assert.Equal(("stale_value", old, "2023-06-01"), (todo.GetProperty("kind").GetString(), todo.GetProperty("id").GetGuid(), todo.GetProperty("on").GetString()));
    }

    [Fact]
    public async Task The_curve_counts_a_property_at_its_price_until_its_first_estimate_and_a_loan_from_its_disbursement_its_principal_paid_in()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app)); // bought 2020-06-15 for 250 000
        await EstimateAsync(app, home, "2025-03-31", "300000");
        await NewPropertyAsync(app, Home(app, "Terrain")); // never estimated: out of the curve, as out of today's net worth
        var later = Home(app, "Studio");
        later["purchaseDate"] = "2025-02-10";
        later["purchasePrice"] = "90000";
        await EstimateAsync(app, await NewPropertyAsync(app, later), "2025-09-30", "95000");
        await NewAsync(app, Short(app, "Prêt travaux")); // disbursed 2025-01-01, 1 000 repaid on each 15th

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/dashboard/net-worth?scope=household");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var points = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("points").EnumerateArray()
            .ToDictionary(p => p.GetProperty("on").GetString()!);

        // Before the first estimate, the price paid; the studio only from its purchase.
        Assert.Equal(("250000.00", "0.00"), (Amount(points["2024-12-31"].GetProperty("value")), Amount(points["2024-12-31"].GetProperty("contributions"))));
        Assert.Equal(("239000.00", "1000.00"), (Amount(points["2025-01-31"].GetProperty("value")), Amount(points["2025-01-31"].GetProperty("contributions"))));
        Assert.Equal("330000.00", Amount(points["2025-02-28"].GetProperty("value"))); // 250 000 + 90 000 − 10 000 owed
        Assert.Equal(("381000.00", "3000.00"), (Amount(points["2025-03-31"].GetProperty("value")), Amount(points["2025-03-31"].GetProperty("contributions"))));
        Assert.Equal(("393000.00", "10000.00"), (Amount(points["2025-10-15"].GetProperty("value")), Amount(points["2025-10-15"].GetProperty("contributions"))));
    }

    [Fact]
    public async Task A_loan_without_a_property_draws_its_balance_alone_from_its_disbursement()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewAsync(app, Short(app, "Prêt travaux")); // disbursed 2025-01-01, 1 000 repaid on each 15th

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/dashboard/net-worth?scope=household");
        var points = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("points").EnumerateArray()
            .ToDictionary(p => p.GetProperty("on").GetString()!);

        Assert.Null(Amount(points["2024-12-31"].GetProperty("value")));
        Assert.Equal(("-11000.00", "1000.00"), (Amount(points["2025-01-31"].GetProperty("value")), Amount(points["2025-01-31"].GetProperty("contributions"))));
        Assert.Equal(("-9000.00", "3000.00"), (Amount(points["2025-03-31"].GetProperty("value")), Amount(points["2025-03-31"].GetProperty("contributions"))));
        Assert.Equal(("-2000.00", "10000.00"), (Amount(points["2025-10-15"].GetProperty("value")), Amount(points["2025-10-15"].GetProperty("contributions"))));
    }

    [Fact]
    public async Task A_property_archived_counts_until_its_archive_day()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app)); // bought 2020-06-15 for 250 000
        await EstimateAsync(app, home, "2025-03-31", "300000");
        await ArchiveAsync(app, home, 2025, 6, 15);

        var points = await HistoryAsync(app, "2025-06-14", "2025-06-15");

        Assert.Equal((300_000m, 1, 250_000m), (Eur(points[0].Values), points[0].Envelopes, Eur(points[0].Moved)));
        Assert.Equal((0m, 0, 250_000m - 300_000m), (Eur(points[1].Values), points[1].Envelopes, Eur(points[1].Moved)));
        Assert.Equal(new Dictionary<string, decimal> { ["real_estate.paid_in"] = 0m, ["real_estate.moved"] = -300_000m, ["real_estate.value"] = 0m },
            Parts(points[0], points[1]));
    }

    [Fact]
    public async Task The_parts_add_up_with_a_purchase_in_the_period()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var studio = Home(app, "Studio");
        studio["purchaseDate"] = "2025-04-01";
        studio["purchasePrice"] = "90000";
        var id = await NewPropertyAsync(app, studio);
        await EstimateAsync(app, id, "2025-09-30", "95000"); // the test clock is on 2025-10-15: no later estimate
        await NewAsync(app, Loan(app, "Prêt studio", "2025-04-01", "2025-04-15")); // 12 000; nine payments by 2025-12-31: 3 000 owed

        var points = await HistoryAsync(app, "2024-12-31", "2025-12-31");

        var change = Eur(points[1].Values) - Eur(points[0].Values);
        var parts = Parts(points[0], points[1]);
        Assert.Equal(95_000m - 3_000m, change);
        Assert.Equal(change, parts.Values.Sum());
        // 9 000 of capital repaid, 90 000 − 12 000 moved into the studio, its 5 000 estimate gain the value.
        Assert.Equal(new Dictionary<string, decimal> { ["real_estate.paid_in"] = 9_000m, ["real_estate.moved"] = 78_000m, ["real_estate.value"] = 5_000m }, parts);
    }

    [Fact]
    public async Task An_archived_loan_moves_its_balance_and_keeps_its_capital_repaid()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await NewAsync(app, Short(app, "Prêt travaux")); // disbursed 2025-01-01, 1 000 repaid on each 15th
        await ArchiveAsync(app, loan, 2025, 6, 20); // six payments made: 6 000 still owed

        var points = await HistoryAsync(app, "2025-06-19", "2025-06-20", "2025-09-30");

        Assert.Equal((-6_000m, 6_000m, -12_000m), (Eur(points[0].Values), Eur(points[0].NetContributions), Eur(points[0].Moved)));
        Assert.Equal((0m, 6_000m, -6_000m, 0), (Eur(points[1].Values), Eur(points[1].NetContributions), Eur(points[1].Moved), points[1].Envelopes));
        Assert.Equal(new Dictionary<string, decimal> { ["real_estate.paid_in"] = 0m, ["real_estate.moved"] = 6_000m, ["real_estate.value"] = 0m },
            Parts(points[0], points[1]));
        Assert.Equal((0m, 6_000m, -6_000m), (Eur(points[2].Values), Eur(points[2].NetContributions), Eur(points[2].Moved))); // frozen after
    }
}
