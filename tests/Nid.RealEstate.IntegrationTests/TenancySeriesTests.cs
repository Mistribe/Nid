using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>The explorer's tenancy series: the rent of a lease chain, with the property's item rights.</summary>
public class TenancySeriesTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> GetAsync(NidApp app, PersonId reader, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await app.RawAsync(reader, HttpMethod.Get, url);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static string[] Keys(JsonElement catalogue) => catalogue.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()!).ToArray();

    private static (string, decimal)[] Points(JsonElement series) => series.GetProperty("points").EnumerateArray()
        .Select(p => (p.GetProperty("date").GetString()!, decimal.Parse(p.GetProperty("value").GetString()!, CultureInfo.InvariantCulture))).ToArray();

    /// <summary>A lease of 800 from 2024-01-01, revised to 820 from 2025-01-01.</summary>
    private static async Task<(Guid Property, Guid First, Guid Last)> NewAsync(NidApp app, string visibility = "shared", string name = "Maison Alice")
    {
        var id = await NewPropertyAsync(app, Home(app, name, visibility));
        var first = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{id}/tenancies",
            new { label = "T2", startsOn = "2024-01-01", endsOn = "2024-12-31", rentMonthly = "800" }));
        var last = Guid.CreateVersion7();
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<IPropertyStore>().AddTenancyAsync(new ItemId(id), last,
            new Tenancy("T2", new LocalDate(2025, 1, 1), null, 820m, 0m, null, null, false, null), Ct, revisedFrom: first);
        return (id, first, last);
    }

    [Fact]
    public async Task Catalogue_lists_one_series_per_chain_for_the_other_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _, last) = await NewAsync(app);

        var found = await GetAsync(app, app.AdultB, "/api/v1/series?q=alice");

        var rent = Assert.Single(found.GetProperty("items").EnumerateArray(), i => i.GetProperty("key").GetString()!.StartsWith("tenancy:"));
        Assert.Equal(($"tenancy:{id}:{last}:rent", id.ToString()), (rent.GetProperty("key").GetString(), rent.GetProperty("item").GetString()));
        Assert.Equal(1, Keys(found).Count(k => k.StartsWith("tenancy:")));
    }

    [Fact]
    public async Task Another_adults_private_property_hides_its_rent_and_404s()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, first, last) = await NewAsync(app, "private");

        Assert.DoesNotContain(Keys(await GetAsync(app, app.AdultB, "/api/v1/series?scope=me")), k => k.Contains(id.ToString()));
        var key = $"tenancy:{id}:{last}:rent";
        var failure = await GetAsync(app, app.AdultB, $"/api/v1/series/points?s={key}", HttpStatusCode.NotFound);
        Assert.Equal(("core.series_not_found", key), (failure.GetProperty("code").GetString(), failure.GetProperty("key").GetString()));

        var denied = Assert.Single(await AuditApi.EventsAsync(app, SecurityEventNames.AccessDenied));
        Assert.Equal(app.AdultB.Value, denied.PersonId!.Value);
        Assert.Equal([id], denied.ItemIds);
    }

    [Fact]
    public async Task The_readers_own_private_property_answers_under_the_household_selector()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _, last) = await NewAsync(app, "private");

        await GetAsync(app, app.AdultA, $"/api/v1/series/points?s=tenancy:{id}:{last}:rent&scope=household");
    }

    [Fact]
    public async Task The_rent_series_is_the_chain_whichever_link_names_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, first, last) = await NewAsync(app);

        var series = (await GetAsync(app, app.AdultA, $"/api/v1/series/points?s=tenancy:{id}:{first}:rent,tenancy:{id}:{last}:rent")).GetProperty("series");

        (string, decimal)[] chain = [("2024-01-01", 800m), ("2025-01-01", 820m)];
        Assert.Equal(chain, Points(series[0]));
        Assert.Equal(chain, Points(series[1]));
        Assert.Equal(("Maison Alice · T2", "currency:EUR"), (series[0].GetProperty("name").GetString(), series[0].GetProperty("unit").GetString()));
    }

    [Fact]
    public async Task A_tenancy_of_another_property_is_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (_, _, last) = await NewAsync(app);
        var other = await NewPropertyAsync(app, Home(app, "Studio"));

        await GetAsync(app, app.AdultA, $"/api/v1/series/points?s=tenancy:{other}:{last}:rent", HttpStatusCode.NotFound);
    }
}
