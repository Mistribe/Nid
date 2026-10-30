using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Core.IntegrationTests.Dashboard;

/// <summary>Tax-per spec §4.6: the PER deadline of 31 December in « À venir ». Today is 2025-11-15, so the window reaches it.</summary>
public class PerDeadlineTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    /// <summary>Counts the profile reads: one per tax computation, as both adults have a notice ceiling (no previous-year pass).</summary>
    private sealed class CountingProfiles(ITaxProfileStore inner, List<int> reads) : ITaxProfileStore
    {
        public Task<StoredTaxProfile?> FindAsync(int year, CancellationToken ct)
        {
            lock (reads) reads.Add(year);
            return inner.FindAsync(year, ct);
        }

        public Task SaveAsync(int year, TaxProfile profile, CancellationToken ct) => inner.SaveAsync(year, profile, ct);
    }

    private static Task<NidApp> StartAsync(PostgresFixture fixture, List<int>? reads = null)
        => NidApp.StartAsync(fixture, services =>
        {
            services.AddSingleton<IClock>(new FixedClock(new LocalDate(2025, 11, 15).At(new LocalTime(12, 0)).InUtc().ToInstant()));
            if (reads is not null)
                services.AddScoped<ITaxProfileStore>(sp => new CountingProfiles(new TaxProfileStore(sp.GetRequiredService<CoreDbContext>()), reads));
        });

    /// <summary>
    /// Adult A pays 8 000 into a PER in 2025; the notices give A 12 000 (10 500 of the year's own, 1 500 unused, 1 000 of it lost on
    /// 31 December) and B 8 000.
    /// </summary>
    private static async Task SeedAsync(NidApp app, bool pooling)
    {
        var per = JsonDocument.Parse(await (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = "PER A", kind = "per", currency = "EUR", visibility = "shared", openedOn = "2020-03-01",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            opening = new { occurredOn = "2024-01-10", amount = new { amount = "1000.00", currency = "EUR" } },
        })).EnsureSuccessStatusCode().Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{per}/flows",
            new { occurredOn = "2025-03-01", kind = "contribution", amount = new { amount = "8000.00", currency = "EUR" } })).EnsureSuccessStatusCode();
        var profile = await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/tax/profile/2025?scope=me", new
        {
            pinel = Array.Empty<object>(), perPooling = pooling,
            perCeilings = new Dictionary<string, string> { [app.AdultA.Value.ToString()] = "12000", [app.AdultB.Value.ToString()] = "8000" },
            perCeilingsExpiring = new Dictionary<string, string> { [app.AdultA.Value.ToString()] = "1000" },
            perUnusedCeilings = new Dictionary<string, string> { [app.AdultA.Value.ToString()] = "1500" },
        });
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
    }

    /// <summary>The PER events of « À venir », as (kind, label, amount).</summary>
    private static async Task<List<(string Kind, string? Label, string Amount)>> PerEventsAsync(NidApp app, PersonId reader, string scope)
    {
        var view = JsonDocument.Parse(await app.GetRawAsync(reader, $"/api/v1/dashboard?scope={scope}")).RootElement;
        return view.GetProperty("upcoming").EnumerateArray().Where(e => e.GetProperty("module").GetString() == "tax")
            .Select(e =>
            {
                Assert.Equal("2025-12-31", e.GetProperty("on").GetString());
                Assert.Equal(JsonValueKind.Null, e.GetProperty("id").ValueKind);
                return (e.GetProperty("kind").GetString()!, e.GetProperty("label").GetString(), e.GetProperty("amount").GetProperty("amount").GetString()!);
            })
            .ToList();
    }

    [Fact]
    public async Task Each_adult_has_what_is_left_to_pay_in_and_what_is_lost()
    {
        await using var app = await StartAsync(fixture);
        await SeedAsync(app, pooling: false);

        var household = await PerEventsAsync(app, app.AdultA, "household");
        var me = await PerEventsAsync(app, app.AdultA, "me");

        // 10 500 + 1 000 − 8 000: the year's own ceiling goes first, so keeping the expiring 1 000 takes 2 420 more.
        Assert.Equal(
            [("per_ceiling_expiring", null, "2420.00"), ("per_deadline", null, "2920.00"), ("per_deadline", null, "8000.00")],
            household.Order().ToList());
        // Moi: the reader's own deadline only.
        Assert.Equal([("per_ceiling_expiring", null, "2420.00"), ("per_deadline", null, "2920.00")], me.Order().ToList());
    }

    [Fact]
    public async Task Under_pooling_the_ceiling_left_is_the_households_in_Foyer_only()
    {
        await using var app = await StartAsync(fixture);
        await SeedAsync(app, pooling: true);

        var household = await PerEventsAsync(app, app.AdultA, "household");
        var me = await PerEventsAsync(app, app.AdultA, "me");

        // 12 000 + 8 000 − 8 000: the household's free ceiling, one event.
        Assert.Equal([("per_ceiling_expiring", null, "2420.00"), ("per_deadline", "household", "10920.00")], household.Order().ToList());
        Assert.Equal([("per_ceiling_expiring", null, "2420.00")], me);
    }

    [Fact]
    public async Task The_dashboard_computes_the_tax_once_for_Impots_and_the_PER_deadline()
    {
        var reads = new List<int>();
        await using var app = await StartAsync(fixture, reads);
        await SeedAsync(app, pooling: false);
        lock (reads) reads.Clear();

        Assert.NotEmpty(await PerEventsAsync(app, app.AdultA, "household"));

        Assert.Equal([2025], reads);
    }

    [Fact]
    public async Task Before_the_window_reaches_31_December_there_is_no_PER_event()
    {
        await using var app = await NidApp.StartAsync(fixture); // 2025-10-15: the window ends on 14 December
        await SeedAsync(app, pooling: false);

        Assert.Empty(await PerEventsAsync(app, app.AdultA, "household"));
    }
}
