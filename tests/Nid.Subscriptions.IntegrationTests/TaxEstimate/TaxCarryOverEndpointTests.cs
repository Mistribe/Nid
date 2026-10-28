using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.TaxEstimate;

/// <summary>Tax assessments spec §5 and §6: the notices feed the next year's estimate and show their gaps with the same year's.</summary>
public class TaxCarryOverEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Route = "/api/v1/tax/assessments";

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static object Declarant(PersonId person, int rank, object? extra = null)
    {
        var map = new Dictionary<string, object?> { ["personId"] = person.Value, ["rank"] = rank };
        if (extra is not null)
            foreach (var p in extra.GetType().GetProperties())
                map[p.Name[..1].ToLowerInvariant() + p.Name[1..]] = p.GetValue(extra);
        return map;
    }

    private static async Task<Guid> AddAsync(NidApp app, PersonId actor, int year, object[] declarants, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["year"] = year, ["declarants"] = declarants };
        if (extra is not null)
            foreach (var p in extra.GetType().GetProperties())
                body[p.Name[..1].ToLowerInvariant() + p.Name[1..]] = p.GetValue(extra);
        var response = await app.RawAsync(actor, HttpMethod.Post, Route, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> EstimateAsync(NidApp app, PersonId reader, int year, string scope)
        => JsonDocument.Parse(await app.GetRawAsync(reader, $"/api/v1/tax?year={year}&scope={scope}")).RootElement;

    private static JsonElement ResultOf(JsonElement estimate, PersonId person)
        => estimate.GetProperty("result").GetProperty("declarants").EnumerateArray().Single(d => d.GetProperty("person").GetGuid() == person.Value);

    [Fact]
    public async Task A_joint_notice_carries_into_next_years_estimate()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await AddAsync(app, app.AdultA, 2024, [Declarant(app.AdultA, 1, new { PerCeilingNext = Eur("4200.00"), PerUnusedOldest = Eur("300.00") }), Declarant(app.AdultB, 2)]);

        foreach (var scope in new[] { "household", "me" })
        {
            var estimate = await EstimateAsync(app, app.AdultA, 2025, scope);

            Assert.Equal(2024, estimate.GetProperty("carriedFrom").GetProperty("year").GetInt32());
            var per = ResultOf(estimate, app.AdultA).GetProperty("per");
            Assert.Equal("4200.00", per.GetProperty("ceiling").GetString());
            Assert.Equal("notice", per.GetProperty("source").GetString());
            Assert.Equal("300.00", per.GetProperty("expiring").GetString());
            Assert.Empty(estimate.GetProperty("profile").GetProperty("perCeilings").EnumerateObject()); // the stored profile is untouched
        }
    }

    [Fact]
    public async Task Solo_notices_carry_only_in_me_and_only_for_their_declarant()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await AddAsync(app, app.AdultA, 2024, [Declarant(app.AdultA, 1, new { PerCeilingNext = Eur("3000.00") })]);

        var household = await EstimateAsync(app, app.AdultA, 2025, "household");
        var me = await EstimateAsync(app, app.AdultA, 2025, "me");
        var other = await EstimateAsync(app, app.AdultB, 2025, "me");

        Assert.Equal(JsonValueKind.Null, household.GetProperty("carriedFrom").ValueKind);
        Assert.Equal("3000.00", ResultOf(me, app.AdultA).GetProperty("per").GetProperty("ceiling").GetString());
        Assert.Equal(JsonValueKind.Null, other.GetProperty("carriedFrom").ValueKind);
        Assert.NotEqual("3000.00", ResultOf(other, app.AdultB).GetProperty("per").GetProperty("ceiling").GetString());
    }

    [Fact]
    public async Task The_rental_deficit_is_only_suggested()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await AddAsync(app, app.AdultA, 2024, [Declarant(app.AdultA, 1), Declarant(app.AdultB, 2)], new { RentalDeficitCarried = Eur("1500.00") });

        var estimate = await EstimateAsync(app, app.AdultA, 2025, "household");

        Assert.Equal("1500.00", estimate.GetProperty("carriedFrom").GetProperty("rentalDeficitCarried").GetString());
        Assert.Equal("0.00", estimate.GetProperty("inputs").GetProperty("rentalDeficitCarried").GetString());
        Assert.Equal("0.00", estimate.GetProperty("profile").GetProperty("rentalDeficitCarried").GetString());
    }

    [Fact]
    public async Task A_notice_ceiling_overrides_the_manual_one_in_the_result_only()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var a = app.AdultA.Value.ToString();
        var put = await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/tax/profile/2025?scope=household", new
        {
            pinel = Array.Empty<object>(), perCeilings = new Dictionary<string, string> { [a] = "8000" }, rentalDeficitCarried = "700",
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var id = await AddAsync(app, app.AdultA, 2024, [Declarant(app.AdultA, 1, new { PerCeilingNext = Eur("4200.00") }), Declarant(app.AdultB, 2)]);

        var estimate = await EstimateAsync(app, app.AdultA, 2025, "household");

        Assert.Equal("8000.00", estimate.GetProperty("profile").GetProperty("perCeilings").GetProperty(a).GetString());
        Assert.Equal("700.00", estimate.GetProperty("profile").GetProperty("rentalDeficitCarried").GetString());
        Assert.Equal("4200.00", ResultOf(estimate, app.AdultA).GetProperty("per").GetProperty("ceiling").GetString());

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Route}/{id}")).StatusCode);
        var back = await EstimateAsync(app, app.AdultA, 2025, "household");

        Assert.Equal("8000.00", ResultOf(back, app.AdultA).GetProperty("per").GetProperty("ceiling").GetString());
    }

    [Fact]
    public async Task A_one_adult_household_counts_the_solo_notice()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            await core.Persons.Where(p => p.Id == app.AdultB.Value).ExecuteUpdateAsync(s => s
                .SetProperty(p => p.OidcSubject, (string?)null).SetProperty(p => p.Email, "b@example.test"));
        await AddAsync(app, app.AdultA, 2024, [Declarant(app.AdultA, 1, new { PerCeilingNext = Eur("3000.00") })]);

        var list = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{Route}?scope=household")).RootElement;
        var estimate = await EstimateAsync(app, app.AdultA, 2025, "household");

        Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal("3000.00", ResultOf(estimate, app.AdultA).GetProperty("per").GetProperty("ceiling").GetString());
    }

    [Fact]
    public async Task Deleting_the_notice_restores_the_profile_values()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await AddAsync(app, app.AdultA, 2024, [Declarant(app.AdultA, 1, new { PerCeilingNext = Eur("4200.00") }), Declarant(app.AdultB, 2)]);
        Assert.Equal("notice", ResultOf(await EstimateAsync(app, app.AdultA, 2025, "household"), app.AdultA).GetProperty("per").GetProperty("source").GetString());

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Route}/{id}")).StatusCode);
        var estimate = await EstimateAsync(app, app.AdultA, 2025, "household");

        Assert.Equal(JsonValueKind.Null, estimate.GetProperty("carriedFrom").ValueKind);
        Assert.Equal("estimated", ResultOf(estimate, app.AdultA).GetProperty("per").GetProperty("source").GetString());
    }

    [Fact]
    public async Task Compute_uses_the_carried_ceilings()
    {
        // Today is 2025-11-15: the dashboard's PER deadline reads the year's result through ComputeAsync.
        await using var app = await NidApp.StartAsync(fixture, services =>
            services.AddSingleton<IClock>(new FixedClock(new LocalDate(2025, 11, 15).At(new LocalTime(12, 0)).InUtc().ToInstant())));
        await AddAsync(app, app.AdultA, 2024, [Declarant(app.AdultA, 1, new { PerCeilingNext = Eur("12000.00") }), Declarant(app.AdultB, 2)]);

        var home = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/dashboard?scope=me")).RootElement;

        var event_ = Assert.Single(home.GetProperty("upcoming").EnumerateArray(), e => e.GetProperty("kind").GetString() == "per_deadline");
        Assert.Equal("12000.00", event_.GetProperty("amount").GetProperty("amount").GetString());
    }

    [Fact]
    public async Task A_year_with_a_matching_notice_shows_its_gaps()
    {
        // A notice of 2025 is issued in 2026.
        await using var app = await NidApp.StartAsync(fixture, services =>
            services.AddSingleton<IClock>(new FixedClock(new LocalDate(2026, 3, 1).At(new LocalTime(12, 0)).InUtc().ToInstant())));
        await AddAsync(app, app.AdultA, 2025, [Declarant(app.AdultA, 1), Declarant(app.AdultB, 2)], new { NetTax = Eur("1000.00") });

        var estimate = await EstimateAsync(app, app.AdultA, 2025, "household");

        var gaps = estimate.GetProperty("gaps");
        var line = Assert.Single(gaps.GetProperty("lines").EnumerateArray());
        Assert.Equal("net_tax", line.GetProperty("code").GetString());
        Assert.Equal("1000.00", line.GetProperty("notice").GetString());
        Assert.Equal("0.00", line.GetProperty("nid").GetString());
        Assert.Equal("1000.00", line.GetProperty("gap").GetString());
        Assert.Empty(gaps.GetProperty("declarants").EnumerateArray());
    }

    [Fact]
    public async Task Another_adults_solo_notice_never_reaches_the_reader()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await AddAsync(app, app.AdultB, 2024, [Declarant(app.AdultB, 1, new { PerCeilingNext = Eur("3000.00") })], new { RentalDeficitCarried = Eur("900.00") });

        foreach (var scope in new[] { "household", "me" })
            Assert.Equal(JsonValueKind.Null, (await EstimateAsync(app, app.AdultA, 2025, scope)).GetProperty("carriedFrom").ValueKind);
    }
}
