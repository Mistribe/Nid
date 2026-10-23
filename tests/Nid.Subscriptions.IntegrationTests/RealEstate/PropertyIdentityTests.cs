using System.Net;
using System.Text.Json;
using ModelContextProtocol.Client;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.IntegrationTests.Admin;
using Nid.Subscriptions.IntegrationTests.Mcp;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.RealEstate.LoanApi;
using static Nid.Subscriptions.IntegrationTests.RealEstate.PropertyApi;

namespace Nid.Subscriptions.IntegrationTests.RealEstate;

/// <summary>Rental receipts spec §2: the postal address of a property, the tenant and landlord identity of a lease.</summary>
public class PropertyIdentityTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Field = "realestate.invalid_property_field";

    private static string? Str(JsonElement e, string name) => e.GetProperty(name).GetString();

    [Fact]
    public async Task The_postal_address_round_trips_is_trimmed_and_clears()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Home(app);
        body["postalAddress"] = "  12 rue des Lilas\n75011 Paris ";
        var id = await NewPropertyAsync(app, body);
        Assert.Equal("12 rue des Lilas\n75011 Paris", Str(await PropertyAsync(app, id), "postalAddress"));

        // Absent keeps it.
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Properties}/{id}", new { version = await VersionAsync(app, id), note = "x" })).StatusCode);
        Assert.Equal("12 rue des Lilas\n75011 Paris", Str(await PropertyAsync(app, id), "postalAddress"));

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Properties}/{id}", new { version = await VersionAsync(app, id), postalAddress = "1 rue B" })).StatusCode);
        Assert.Equal("1 rue B", Str(await PropertyAsync(app, id), "postalAddress"));

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Properties}/{id}", new { version = await VersionAsync(app, id), clearPostalAddress = true })).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await PropertyAsync(app, id)).GetProperty("postalAddress").ValueKind);
    }

    [Fact]
    public async Task A_postal_address_over_400_characters_is_refused_on_its_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Home(app);
        body["postalAddress"] = new string('a', 401);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, Field, "postalAddress"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Properties, body)));

        body["postalAddress"] = new string('a', 400);
        var id = await NewPropertyAsync(app, body);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, Field, "postalAddress"), await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch,
            $"{Properties}/{id}", new { version = await VersionAsync(app, id), postalAddress = new string('a', 401) })));
    }

    [Fact]
    public async Task The_tenancy_identity_round_trips_and_a_put_without_it_keeps_it() // Review Focus #4
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        var url = $"{Properties}/{id}/tenancies";
        var tenancy = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, url, new
        {
            label = "T2", startsOn = "2021-01-01", rentMonthly = "900",
            tenantNames = " Mme Martin ", landlordName = "Alice et Bob", landlordAddress = "1 rue A\n75001 Paris",
        }));
        async Task<JsonElement> Lease() => Only(await PropertyAsync(app, id), "tenancies");
        Assert.Equal(("Mme Martin", "Alice et Bob", "1 rue A\n75001 Paris"),
            (Str(await Lease(), "tenantNames"), Str(await Lease(), "landlordName"), Str(await Lease(), "landlordAddress")));

        // The old web form and MCP correct_tenancy send none of the three: nothing is lost.
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/{tenancy}", new
        {
            label = "Renamed", startsOn = "2021-01-01", rentMonthly = "900", version = (await Lease()).GetProperty("version").GetUInt32(),
        })).StatusCode);
        Assert.Equal(("Renamed", "Mme Martin", "Alice et Bob", "1 rue A\n75001 Paris"),
            (Str(await Lease(), "label"), Str(await Lease(), "tenantNames"), Str(await Lease(), "landlordName"), Str(await Lease(), "landlordAddress")));

        // Present overrides, and "" clears.
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/{tenancy}", new
        {
            label = "Renamed", startsOn = "2021-01-01", rentMonthly = "900", tenantNames = "M. Durand", landlordName = "",
            version = (await Lease()).GetProperty("version").GetUInt32(),
        })).StatusCode);
        var after = await Lease();
        Assert.Equal(("M. Durand", JsonValueKind.Null, "1 rue A\n75001 Paris"),
            (Str(after, "tenantNames"), after.GetProperty("landlordName").ValueKind, Str(after, "landlordAddress")));
    }

    [Fact]
    public async Task The_month_rents_are_tracked_from_round_trips_a_put_without_it_keeps_it_and_empty_clears_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        var url = $"{Properties}/{id}/tenancies";
        var tenancy = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, url,
            new { label = "T2", startsOn = "2021-01-15", rentMonthly = "900", rentsTrackedFrom = "2025-02" }));
        async Task<JsonElement> Lease() => Only(await PropertyAsync(app, id), "tenancies");
        Assert.Equal("2025-02", Str(await Lease(), "rentsTrackedFrom"));

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/{tenancy}", new
        {
            label = "Renamed", startsOn = "2021-01-15", rentMonthly = "900", version = (await Lease()).GetProperty("version").GetUInt32(),
        })).StatusCode);
        Assert.Equal("2025-02", Str(await Lease(), "rentsTrackedFrom"));

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/{tenancy}", new
        {
            label = "Renamed", startsOn = "2021-01-15", rentMonthly = "900", rentsTrackedFrom = "", version = (await Lease()).GetProperty("version").GetUInt32(),
        })).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Lease()).GetProperty("rentsTrackedFrom").ValueKind);

        // The lease's own month is allowed, an earlier one or a malformed one is refused on the field.
        foreach (var refused in new[] { "2020-12", "2025-13", "2025-02-01" })
            Assert.Equal((HttpStatusCode.UnprocessableEntity, Field, "rentsTrackedFrom"), await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Put,
                $"{url}/{tenancy}", new { label = "T2", startsOn = "2021-01-15", rentMonthly = "900", rentsTrackedFrom = refused,
                    version = (await Lease()).GetProperty("version").GetUInt32() })));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/{tenancy}", new
        {
            label = "T2", startsOn = "2021-01-15", rentMonthly = "900", rentsTrackedFrom = "2021-01", version = (await Lease()).GetProperty("version").GetUInt32(),
        })).StatusCode);
        Assert.Equal("2021-01", Str(await Lease(), "rentsTrackedFrom"));
    }

    [Fact]
    public async Task A_tenancy_name_over_200_or_an_address_over_400_characters_is_refused_on_its_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        var url = $"{Properties}/{id}/tenancies";
        string Long(int length) => new('a', length);

        foreach (var (field, body) in new (string, object)[]
                 {
                     ("tenantNames", new { label = "T2", startsOn = "2021-01-01", rentMonthly = "900", tenantNames = Long(201) }),
                     ("landlordName", new { label = "T2", startsOn = "2021-01-01", rentMonthly = "900", landlordName = Long(201) }),
                     ("landlordAddress", new { label = "T2", startsOn = "2021-01-01", rentMonthly = "900", landlordAddress = Long(401) }),
                 })
            Assert.Equal((HttpStatusCode.UnprocessableEntity, Field, field), await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, url, body)));

        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, url,
            new { label = "T2", startsOn = "2021-01-01", rentMonthly = "900", tenantNames = Long(200), landlordName = Long(200), landlordAddress = Long(400) }));
    }

    [Fact]
    public async Task The_identity_fields_never_reach_mcp_and_rest_still_returns_them()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Home(app);
        body["postalAddress"] = "12 rue des Lilas\n75011 Paris";
        var id = await NewPropertyAsync(app, body);
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{id}/tenancies", new
        {
            label = "T2", startsOn = "2021-01-01", rentMonthly = "900", tenantNames = "Mme Martin", landlordName = "Alice et Bob", landlordAddress = "1 rue A",
        }));

        var rest = await PropertyAsync(app, id);
        Assert.Equal("12 rue des Lilas\n75011 Paris", Str(rest, "postalAddress"));
        Assert.Equal("Mme Martin", Str(Only(rest, "tenancies"), "tenantNames"));

        var read = await TokenRouteTests.CreateAsync(app, app.AdultA, TokenPresets.Read);
        await using var client = await McpHarness.ClientAsync(app, read.Secret);
        var result = await client.CallToolAsync("get_property", new Dictionary<string, object?> { ["id"] = id });
        Assert.True(result.IsError != true, McpHarness.TextOf(result));
        var text = McpHarness.TextOf(result);
        foreach (var secret in new[] { "Lilas", "Martin", "Alice et Bob", "1 rue A" })
            Assert.DoesNotContain(secret, text);
        var mcp = JsonDocument.Parse(text).RootElement;
        Assert.Equal(JsonValueKind.Null, mcp.GetProperty("postalAddress").ValueKind);
        var lease = Only(mcp, "tenancies");
        Assert.Equal("T2", Str(lease, "label"));
        foreach (var name in new[] { "tenantNames", "landlordName", "landlordAddress" })
            Assert.Equal(JsonValueKind.Null, lease.GetProperty(name).ValueKind);
    }
}
