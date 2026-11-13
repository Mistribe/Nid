using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Childcare.IntegrationTests;

/// <summary>The arrangement of the API tests (from 2025-09-01, 4,30 € net, Mon/Tue/Thu/Fri 08:00–17:30, 46 weeks) and its statement calls.</summary>
internal static class ChildcareApi
{
    public const string Url = "/api/v1/childcare";

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary><paramref name="weekTypes"/>: the revision's week types in place of the one « Semaine type » of 46 weeks.</summary>
    public static async Task<string> CreateAsync(NidApp app, string visibility = "shared", object[]? weekTypes = null)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Url, new
        {
            name = "Garde de Léa", childId = app.Dependent.Value,
            carer = new { name = "Martine", pajemploi = "P123", approval = (string?)null },
            terms = new
            {
                from = "2025-09-01", netHourlyRate = "4.30", grossHourlyRate = (string?)null, overtimePct = "10", upkeepPer9h = "3.80", lunchPrice = "3.20",
                weekTypes = weekTypes ?? [new { name = "Semaine type", weeks = 46, schedule = Schedule }],
            },
            gaps = Array.Empty<object>(), visibility,
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return $"{Url}/{(await JsonAsync(response)).GetProperty("id").GetGuid()}";
    }

    /// <summary>Mon/Tue/Thu/Fri 08:00–17:30 with lunch.</summary>
    public static readonly object[] Schedule = [.. new[] { 1, 2, 4, 5 }.Select(d => new { day = d, arrival = "08:00", departure = "17:30", lunch = true })];

    public static async Task<JsonElement> GetAsync(NidApp app, string url, PersonId? actor = null)
    {
        var response = await app.RawAsync(actor ?? app.AdultA, HttpMethod.Get, url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    /// <summary>Saves the month as read with this CMG and these contributions, then validates it; answers the validated month.</summary>
    public static async Task<JsonElement> ValidateAsync(NidApp app, string arrangement, string month, string cmg = "100", string? contributions = null, string? exemption = null)
    {
        var url = $"{arrangement}/statements/{month}";
        var read = await GetAsync(app, url);
        var days = read.GetProperty("days").EnumerateArray().Select(d => new
        {
            date = d.GetProperty("date").GetString(), present = d.GetProperty("present").GetBoolean(), arrival = d.GetProperty("arrival").GetString(),
            departure = d.GetProperty("departure").GetString(), lunch = d.GetProperty("lunch").GetBoolean(), absence = d.GetProperty("absence").GetString(),
        }).ToList();
        var saved = await app.RawAsync(app.AdultA, HttpMethod.Put, url, new { days, mileage = "0", cmg, contributions, exemption, version = read.GetProperty("version").GetUInt32() });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var validated = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/validate", new { version = (await JsonAsync(saved)).GetProperty("version").GetUInt32() });
        Assert.Equal(HttpStatusCode.OK, validated.StatusCode);
        return await JsonAsync(validated);
    }

    public static async Task ReopenAsync(NidApp app, string arrangement, string month)
        => Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{arrangement}/statements/{month}/reopen")).StatusCode);
}
