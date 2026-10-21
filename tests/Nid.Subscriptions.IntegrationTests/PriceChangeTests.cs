using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>1E2a design §2.3: close the open phase and open the next, all or nothing.</summary>
public class PriceChangeTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static object Change(string startsOn, string amount = "15.99") => new { startsOn, price = new { amount, currency = "EUR" } };

    [Fact]
    public async Task The_open_phase_ends_where_the_new_price_starts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m); // open phase from 2025-01-01, monthly, household

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/price-changes", Change("2025-06-01"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var phases = await subs.Phases.Where(p => p.ItemId == id.Value).OrderBy(p => p.StartsOn).ToListAsync();
        Assert.Equal(2, phases.Count);
        Assert.Equal((new NodaTime.LocalDate(2025, 6, 1), (NodaTime.LocalDate?)null, 15.99m, "month", "household"),
            (phases[1].StartsOn, phases[1].EndsOn, phases[1].PriceAmount, phases[1].EveryUnit, phases[1].PayerType));
        Assert.Equal(new NodaTime.LocalDate(2025, 6, 1), phases[0].EndsOn);
    }

    [Theory]
    [InlineData("2024-06-01", "subscriptions.no_open_phase")]
    [InlineData("2025-01-01", "subscriptions.phase_range")]
    public async Task A_date_with_no_phase_to_replace_is_refused_at_startsOn(string startsOn, string code)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/price-changes", Change(startsOn));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateArray());
        Assert.Equal(("startsOn", code), (error.GetProperty("path").GetString(), error.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task A_refused_new_phase_leaves_the_open_one_open()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/price-changes",
            new { startsOn = "2025-06-01", price = new { amount = "15.99", currency = "EUR" }, maxCycles = 0 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        var phase = Assert.Single(await subs.Phases.Where(p => p.ItemId == id.Value).ToListAsync());
        Assert.Null(phase.EndsOn);
    }

    [Fact]
    public async Task An_adult_who_can_read_but_not_write_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m); // shared, owned by A alone: B reads, A writes

        var response = await app.RawAsync(app.AdultB, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/price-changes", Change("2025-06-01"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<Guid> FixedTermAsync(NidApp app, int cycles)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", new
        {
            displayName = "Engagement", visibility = "shared", autoRenew = true,
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            phases = new[]
            {
                new
                {
                    startsOn = "2025-01-01", price = new { amount = "10", currency = "EUR" }, every = new { count = 1, unit = "month" },
                    maxCycles = cycles, payer = new { type = "household" },
                },
            },
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Final review: a fixed-term plan stays fixed-term across a price change, and the old phase spends its cycles.</summary>
    [Fact]
    public async Task A_cycle_limit_is_split_between_the_closed_and_the_new_phase()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await FixedTermAsync(app, 12);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id}/price-changes", Change("2025-06-01"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await app.GetAsync<JsonElement>(app.AdultA, $"/api/v1/subscriptions/{id}");
        var cycles = detail.GetProperty("phases").EnumerateArray().Select(p => p.GetProperty("maxCycles").GetInt32()).ToList();
        Assert.Equal([5, 7], cycles);
        Assert.DoesNotContain(detail.GetProperty("findings").EnumerateArray(), f => f.GetProperty("code").GetString() == "subscriptions.phase_cycles_unspent");
    }

    [Fact]
    public async Task A_phase_whose_cycles_are_all_spent_is_not_open()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await FixedTermAsync(app, 3); // January to March, then nothing

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id}/price-changes", Change("2025-06-01"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateArray());
        Assert.Equal("subscriptions.no_open_phase", error.GetProperty("code").GetString());
    }
}
