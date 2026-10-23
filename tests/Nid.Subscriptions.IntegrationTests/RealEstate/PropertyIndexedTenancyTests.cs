using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.Subscriptions.IntegrationTests.RealEstate.LoanApi;
using static Nid.Subscriptions.IntegrationTests.RealEstate.PropertyApi;

namespace Nid.Subscriptions.IntegrationTests.RealEstate;

/// <summary>Rental spec §3.2: the indexed tenancy, the energy class and the guards of a revised chain.</summary>
public class PropertyIndexedTenancyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Field = "realestate.invalid_property_field", Revised = "realestate.tenancy_revised";

    private static object Lease(string startsOn, string? endsOn = null, string rent = "900", string label = "T2", uint? version = null,
        bool indexed = false, int? quarter = null)
        => new { label, startsOn, endsOn, rentMonthly = rent, indexed, irlReferenceQuarter = quarter, version };

    [Fact]
    public async Task An_indexed_tenancy_reads_back_and_its_quarter_is_checked()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        var tenancies = $"{Properties}/{id}/tenancies";

        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, tenancies, Lease("2021-01-01", "2023-01-01", indexed: true, quarter: 2)));
        var view = Only(await PropertyAsync(app, id), "tenancies");
        Assert.True(view.GetProperty("indexed").GetBoolean());
        Assert.Equal(2, view.GetProperty("irlReferenceQuarter").GetInt32());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("revisedFrom").ValueKind);

        Assert.Equal((HttpStatusCode.UnprocessableEntity, Field, "irlReferenceQuarter"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, tenancies, Lease("2023-01-01", quarter: 5))));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, Field, "irlReferenceQuarter"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, tenancies, Lease("2023-01-01", indexed: true))));
        // A quarter without the switch is kept.
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, tenancies, Lease("2023-01-01", quarter: 3)));
        Assert.Equal(2, (await PropertyAsync(app, id)).GetProperty("tenancies").GetArrayLength());
    }

    [Fact]
    public async Task A_put_keeps_the_indexation_unless_it_says_otherwise()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        var url = $"{Properties}/{id}/tenancies";
        var tenancy = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, url, Lease("2021-01-01", indexed: true, quarter: 2)));
        async Task<(bool, int?)> State() => (Only(await PropertyAsync(app, id), "tenancies") is var t
            ? (t.GetProperty("indexed").GetBoolean(), t.GetProperty("irlReferenceQuarter").ValueKind == JsonValueKind.Null ? null : t.GetProperty("irlReferenceQuarter").GetInt32())
            : default);
        async Task<uint> Version() => Only(await PropertyAsync(app, id), "tenancies").GetProperty("version").GetUInt32();

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/{tenancy}",
            new { label = "Renamed", startsOn = "2021-01-01", rentMonthly = "900", version = await Version() })).StatusCode);
        Assert.Equal((true, 2), await State());

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/{tenancy}",
            new { label = "Renamed", startsOn = "2021-01-01", rentMonthly = "900", irlReferenceQuarter = 3, version = await Version() })).StatusCode);
        Assert.Equal((true, 3), await State());

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{url}/{tenancy}",
            new { label = "Renamed", startsOn = "2021-01-01", rentMonthly = "900", indexed = false, version = await Version() })).StatusCode);
        Assert.Equal((false, 3), await State());
    }

    [Fact]
    public async Task The_energy_class_round_trips_and_is_one_of_A_to_G()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Home(app);
        body["energyClass"] = "F";
        var id = await NewPropertyAsync(app, body);
        Assert.Equal("F", S(await PropertyAsync(app, id), "energyClass"));

        var version = await VersionAsync(app, id);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, Field, "energyClass"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Properties}/{id}", new { version, energyClass = "H" })));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Properties}/{id}", new { version, energyClass = "C" })).StatusCode);
        Assert.Equal("C", S(await PropertyAsync(app, id), "energyClass"));
        version = await VersionAsync(app, id);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{Properties}/{id}", new { version, clearEnergyClass = true })).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await PropertyAsync(app, id)).GetProperty("energyClass").ValueKind);

        body["energyClass"] = "H";
        Assert.Equal((HttpStatusCode.UnprocessableEntity, Field, "energyClass"),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Properties, body)));
    }

    [Fact]
    public async Task A_tenancy_with_a_successor_keeps_its_rent_and_dates_and_is_not_deleted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        var tenancies = $"{Properties}/{id}/tenancies";
        var old = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, tenancies, Lease("2021-01-01", "2022-01-01", indexed: true, quarter: 2)));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<IPropertyStore>().AddTenancyAsync(new ItemId(id), Guid.CreateVersion7(),
                new Tenancy("T2", new LocalDate(2022, 1, 1), null, 910m, 0m, null, null, true, 2), default, revisedFrom: old);
        }

        var views = (await PropertyAsync(app, id)).GetProperty("tenancies").EnumerateArray().ToList();
        Assert.Equal(old, views[1].GetProperty("revisedFrom").GetGuid());
        var version = views[0].GetProperty("version").GetUInt32();

        foreach (var (field, body) in new (string, object)[]
                 {
                     ("rentMonthly", Lease("2021-01-01", "2022-01-01", "950", version: version, indexed: true, quarter: 2)),
                     ("startsOn", Lease("2021-02-01", "2022-01-01", version: version, indexed: true, quarter: 2)),
                     ("endsOn", Lease("2021-01-01", "2021-12-01", version: version, indexed: true, quarter: 2)),
                 })
            Assert.Equal((HttpStatusCode.UnprocessableEntity, Revised, field),
                await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, $"{tenancies}/{old}", body)));

        // Another field stays editable.
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{tenancies}/{old}",
            Lease("2021-01-01", "2022-01-01", label: "Bail 2021", version: version, indexed: true, quarter: 2))).StatusCode);

        views = (await PropertyAsync(app, id)).GetProperty("tenancies").EnumerateArray().ToList();
        Assert.Equal((HttpStatusCode.UnprocessableEntity, Revised, (string?)null),
            await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{tenancies}/{old}?version={views[0].GetProperty("version").GetUInt32()}")));

        // The revision itself is not deleted either: only the undo (a later route) removes it.
        Assert.Equal((HttpStatusCode.UnprocessableEntity, Revised, (string?)null), await RefusalAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete,
            $"{tenancies}/{views[1].GetProperty("id").GetGuid()}?version={views[1].GetProperty("version").GetUInt32()}")));
    }
}
