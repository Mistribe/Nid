using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Core.IntegrationTests.TaxEstimate;

/// <summary>Issue #410: GET /api/v1/tax/changes, last year read from the notice when there is one (today: 2026-11-05).</summary>
public class TaxChangesEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private static Task<NidApp> StartAsync(PostgresFixture fixture)
        => NidApp.StartAsync(fixture, services => services.AddSingleton<IClock>(new FixedClock(Instant.FromUtc(2026, 11, 5, 12, 0))));

    private static async Task<JsonElement> ChangesAsync(NidApp app, int year, string scope = "household")
        => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/tax/changes?year={year}&scope={scope}")).RootElement;

    [Fact]
    public async Task The_notices_salaries_and_tax_stand_for_last_year()
    {
        await using var app = await StartAsync(fixture);
        var notice = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/tax/assessments", new
        {
            year = 2025,
            declarants = new object[]
            {
                new { personId = app.AdultA.Value, rank = 1, netSalaries = new { amount = "30000.00", currency = "EUR" } },
                new { personId = app.AdultB.Value, rank = 2 },
            },
            netTax = new { amount = "1200.00", currency = "EUR" },
        });
        Assert.Equal(HttpStatusCode.Created, notice.StatusCode);

        var changes = await ChangesAsync(app, 2026);

        Assert.Equal(2026, changes.GetProperty("year").GetInt32());
        Assert.Equal("1200.00", changes.GetProperty("taxBefore").GetString());
        Assert.True(changes.GetProperty("taxBeforeFromNotice").GetBoolean());
        var line = Assert.Single(changes.GetProperty("lines").EnumerateArray());
        Assert.Equal("salaries", line.GetProperty("code").GetString());
        Assert.Equal(app.AdultA.Value, line.GetProperty("person").GetGuid());
        Assert.Equal(("30000.00", "0.00"), (line.GetProperty("before").GetString(), line.GetProperty("now").GetString()));
        Assert.True(line.GetProperty("fromNotice").GetBoolean());
        Assert.StartsWith("-", line.GetProperty("effect").GetString());
    }

    [Fact]
    public async Task The_first_year_of_the_rules_has_nothing_to_compare_without_a_notice()
    {
        await using var app = await StartAsync(fixture);

        var changes = await ChangesAsync(app, 2025, "me");

        Assert.Empty(changes.GetProperty("lines").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, changes.GetProperty("taxBefore").ValueKind);
    }
}
