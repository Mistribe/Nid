using System.Text.Json;

namespace Nid.TestSupport;

/// <summary>Shared by the projection endpoint tests: the household's assumptions and a monthly income source.</summary>
public static class ProjectionApi
{
    /// <summary>The household's assumptions at their class rates, 2 % inflation, cash savings and <paramref name="monthlySavings"/> as S₀.</summary>
    public static async Task HouseholdSavingsAsync(NidApp app, string monthlySavings)
    {
        var assumptions = await app.GetAsync<JsonElement>(app.AdultA, "/api/v1/core/projection-assumptions");
        (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/core/projection-assumptions", new
        {
            classRates = assumptions.GetProperty("classRates"), inflation = "2", monthlySavings, savingsClass = "cash", freedPaymentsToSavings = true,
        })).EnsureSuccessStatusCode();
    }

    /// <summary>A shared EUR source of A's, one open phase paying <paramref name="amount"/> a month from <paramref name="startsOn"/>.</summary>
    public static async Task IncomeSourceAsync(NidApp app, string name, string kind, string startsOn, string amount)
        => (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/income/sources", new Dictionary<string, object?>
        {
            ["displayName"] = name, ["kind"] = kind, ["currency"] = "EUR", ["visibility"] = "shared",
            ["owners"] = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            ["phases"] = new[]
            {
                new { label = name, startsOn, netAmount = new { amount, currency = "EUR" }, every = new { count = 1, unit = "month" } },
            },
        })).EnsureSuccessStatusCode();
}
