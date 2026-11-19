using System.Net;
using System.Text.Json;
using Nid.TestSupport;
using static Nid.Childcare.IntegrationTests.ChildcareApi;

namespace Nid.Childcare.IntegrationTests;

/// <summary>The legal minimums the terms fall below, on the contract and on a month (today: 2025-10-15, the June 2026 revaluation already known).</summary>
public class ChildcareMinimumsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static Dictionary<string, object?> Terms(string from, string net, string upkeep) => new()
    {
        ["from"] = from, ["netHourlyRate"] = net, ["grossHourlyRate"] = null, ["overtimePct"] = "10", ["upkeepPer9h"] = upkeep,
        ["lunchPrice"] = "3.20", ["weekTypes"] = new[] { new { name = "Semaine type", weeks = 46, schedule = Schedule } },
    };

    private static IReadOnlyList<(string, string, string, string)> Minimums(JsonElement view)
        => [.. view.GetProperty("belowMinimums").EnumerateArray().Select(m => (m.GetProperty("code").GetString()!, m.GetProperty("since").GetString()!,
            m.GetProperty("minimum").GetString()!, m.GetProperty("value").GetString()!))];

    [Fact]
    public async Task Contract_warns_of_a_published_revaluation_until_a_revision_meets_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = await CreateAsync(app);
        // 3,80 € meets the 2025 minimum (90 % of 4,22 €) but not the 3,92 € of June 2026.
        Assert.Equal([("childcare.below_minimum_upkeep", "2026-06-01", "3.92", "3.80")], Minimums(await GetAsync(app, url)));
        Assert.Empty(Minimums(await GetAsync(app, $"{url}/statements/2025-09")));

        var revised = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/revisions", new { terms = Terms("2026-06-01", "4.30", "4.00") });
        Assert.Equal(HttpStatusCode.OK, revised.StatusCode);
        Assert.Empty(Minimums(await GetAsync(app, url)));
    }

    [Fact]
    public async Task Month_under_the_hourly_minimum_warns_without_refusing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, Url, new
        {
            name = "Garde de Léa", childId = app.Dependent.Value, carer = new { name = "Martine", pajemploi = (string?)null, approval = (string?)null },
            terms = Terms("2025-09-01", "2.50", "3.80"), gaps = Array.Empty<object>(), visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var url = $"{Url}/{(await JsonAsync(created)).GetProperty("id").GetGuid()}";

        Assert.Equal([("childcare.below_minimum_net_rate", "2025-04-01", "2.84", "2.50")], Minimums(await GetAsync(app, $"{url}/statements/2025-09")));
        Assert.Equal([("childcare.below_minimum_net_rate", "2025-04-01", "2.84", "2.50")], Minimums(await ValidateAsync(app, url, "2025-09")));
    }
}
