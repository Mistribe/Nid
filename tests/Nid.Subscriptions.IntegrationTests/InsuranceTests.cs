using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>Design 2026-10-30-contract-terms-insurance §8 and §10: the insurance PUT, DELETE and its read, with the link to a property.</summary>
public class InsuranceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static string Url(ItemId id) => $"/api/v1/subscriptions/{id.Value}";

    private static string InsuranceUrl(ItemId id) => $"{Url(id)}/insurance";

    private static object Home(Guid? propertyId = null, params string[] guarantees) => new
    {
        kind = "home",
        propertyId,
        insuredLabel = "Main residence",
        policyNumber = "HX-123",
        insurerContact = "01 02 03 04 05",
        guarantees = guarantees.Select(g => new { label = g, ceiling = "100 000 EUR", deductible = (string?)"150 EUR" }).ToArray(),
    };

    private static async Task<JsonElement> Body(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<JsonElement> InsuranceOf(NidApp app, PersonId reader, ItemId id)
        => (await app.GetAsync<JsonElement>(reader, Url(id))).GetProperty("insurance");

    [Fact]
    public async Task Put_then_get()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);
        var property = await app.NewPropertyAsync("Maison", Visibility.Shared, (app.AdultA, 10_000));

        var put = await app.RawAsync(app.AdultA, HttpMethod.Put, InsuranceUrl(id), Home(property.Value, "Fire", "Theft"));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(id.Value, (await Body(put)).GetProperty("id").GetGuid());
        var insurance = await InsuranceOf(app, app.AdultA, id);
        Assert.Equal("home", insurance.GetProperty("kind").GetString());
        Assert.Equal(property.Value, insurance.GetProperty("property").GetProperty("id").GetGuid());
        Assert.Equal("Maison", insurance.GetProperty("property").GetProperty("name").GetString());
        Assert.Equal("Main residence", insurance.GetProperty("insuredLabel").GetString());
        Assert.Equal("HX-123", insurance.GetProperty("policyNumber").GetString());
        Assert.Equal("01 02 03 04 05", insurance.GetProperty("insurerContact").GetString());
        var guarantees = insurance.GetProperty("guarantees").EnumerateArray().ToList();
        Assert.Equal(["Fire", "Theft"], guarantees.Select(g => g.GetProperty("label").GetString()));
        Assert.Equal(("100 000 EUR", "150 EUR"), (guarantees[0].GetProperty("ceiling").GetString(), guarantees[0].GetProperty("deductible").GetString()));
    }

    [Fact]
    public async Task A_subscription_without_insurance_has_none()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);

        Assert.Equal(JsonValueKind.Null, (await InsuranceOf(app, app.AdultA, id)).ValueKind);
    }

    [Fact]
    public async Task Put_replaces()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);
        var property = await app.NewPropertyAsync("Maison", Visibility.Shared, (app.AdultA, 10_000));
        (await app.RawAsync(app.AdultA, HttpMethod.Put, InsuranceUrl(id), Home(property.Value, "Fire", "Theft"))).EnsureSuccessStatusCode();

        (await app.RawAsync(app.AdultA, HttpMethod.Put, InsuranceUrl(id), new { kind = "car", guarantees = new[] { new { label = "Third party" } } }))
            .EnsureSuccessStatusCode();

        var insurance = await InsuranceOf(app, app.AdultA, id);
        Assert.Equal("car", insurance.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, insurance.GetProperty("property").ValueKind);
        Assert.Equal(JsonValueKind.Null, insurance.GetProperty("policyNumber").ValueKind);
        Assert.Equal(["Third party"], insurance.GetProperty("guarantees").EnumerateArray().Select(g => g.GetProperty("label").GetString()));
    }

    [Fact]
    public async Task Delete_clears()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);
        (await app.RawAsync(app.AdultA, HttpMethod.Put, InsuranceUrl(id), Home(null, "Fire"))).EnsureSuccessStatusCode();

        var delete = await app.RawAsync(app.AdultA, HttpMethod.Delete, InsuranceUrl(id));

        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await InsuranceOf(app, app.AdultA, id)).ValueKind);
    }

    private static async Task<(HttpStatusCode Status, string Path, string Code)> RefusedAsync(NidApp app, PersonId author, ItemId id, Guid propertyId)
    {
        var response = await app.RawAsync(author, HttpMethod.Put, InsuranceUrl(id), Home(propertyId, "Fire"));
        var error = Assert.Single((await Body(response)).GetProperty("errors").EnumerateArray());
        return (response.StatusCode, error.GetProperty("path").GetString()!, error.GetProperty("code").GetString()!);
    }

    [Fact]
    public async Task Link_to_a_property_the_author_cannot_read_answers_as_unknown()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);
        var secret = await app.NewPropertyAsync("Secret", Visibility.Private, (app.AdultB, 10_000));

        var unreadable = await RefusedAsync(app, app.AdultA, id, secret.Value);
        var unknown = await RefusedAsync(app, app.AdultA, id, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unreadable.Status);
        Assert.Equal("propertyId", unreadable.Path);
        Assert.Equal(unknown, unreadable);
        Assert.Equal(JsonValueKind.Null, (await InsuranceOf(app, app.AdultA, id)).ValueKind);
    }

    [Fact]
    public async Task Link_to_a_non_property_item_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);
        var loan = await app.NewLoanAsync("Pret", Visibility.Shared, (app.AdultA, 10_000));

        var refused = await RefusedAsync(app, app.AdultA, id, loan.Value);
        var unknown = await RefusedAsync(app, app.AdultA, id, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
        Assert.Equal("propertyId", refused.Path);
        Assert.Equal(unknown, refused);
    }

    [Fact]
    public async Task Keeping_an_existing_link_is_not_a_new_link()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewOwnedSubscriptionAsync("Home insurance", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        var property = await app.NewPropertyAsync("Private house", Visibility.Private, (app.AdultA, 10_000));
        (await app.RawAsync(app.AdultA, HttpMethod.Put, InsuranceUrl(id), Home(property.Value, "Fire"))).EnsureSuccessStatusCode();

        // B writes the subscription but cannot read the property: the same id is not a new link.
        var put = await app.RawAsync(app.AdultB, HttpMethod.Put, InsuranceUrl(id), Home(property.Value, "Fire", "Flood"));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var insurance = await InsuranceOf(app, app.AdultA, id);
        Assert.Equal(2, insurance.GetProperty("guarantees").GetArrayLength());
        Assert.Equal(property.Value, insurance.GetProperty("property").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Keep_property_leaves_a_hidden_link_in_place()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewOwnedSubscriptionAsync("Home insurance", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        var property = await app.NewPropertyAsync("Private house", Visibility.Private, (app.AdultA, 10_000));
        (await app.RawAsync(app.AdultA, HttpMethod.Put, InsuranceUrl(id), Home(property.Value, "Fire"))).EnsureSuccessStatusCode();

        // B cannot read the property, so the form sends no id but asks to keep the link.
        var put = await app.RawAsync(app.AdultB, HttpMethod.Put, InsuranceUrl(id), new
        {
            kind = "home", keepProperty = true, policyNumber = "HX-999", guarantees = new[] { new { label = "Fire" }, new { label = "Flood" } },
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var insurance = await InsuranceOf(app, app.AdultA, id);
        Assert.Equal(property.Value, insurance.GetProperty("property").GetProperty("id").GetGuid());
        Assert.Equal("HX-999", insurance.GetProperty("policyNumber").GetString());
        Assert.Equal(2, insurance.GetProperty("guarantees").GetArrayLength());
    }

    [Fact]
    public async Task Keep_property_with_a_property_id_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);
        var property = await app.NewPropertyAsync("Maison", Visibility.Shared, (app.AdultA, 10_000));

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, InsuranceUrl(id),
                                          new { kind = "home", propertyId = property.Value, keepProperty = true, guarantees = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await InsuranceOf(app, app.AdultA, id)).ValueKind);
    }

    [Fact]
    public async Task A_different_unreadable_property_is_refused_even_when_a_link_is_stored()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewOwnedSubscriptionAsync("Home insurance", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        var stored = await app.NewPropertyAsync("Private house", Visibility.Private, (app.AdultA, 10_000));
        var other = await app.NewPropertyAsync("Other private house", Visibility.Private, (app.AdultA, 10_000));
        (await app.RawAsync(app.AdultA, HttpMethod.Put, InsuranceUrl(id), Home(stored.Value, "Fire"))).EnsureSuccessStatusCode();

        // B reads neither property: swapping the stored link for another one is not "keeping" it.
        var unreadable = await RefusedAsync(app, app.AdultB, id, other.Value);
        var unknown = await RefusedAsync(app, app.AdultB, id, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unreadable.Status);
        Assert.Equal("propertyId", unreadable.Path);
        Assert.Equal(unknown, unreadable);
        Assert.Equal(stored.Value, (await InsuranceOf(app, app.AdultA, id)).GetProperty("property").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task The_history_of_the_subscription_hides_the_linked_property_from_a_co_owner_who_cannot_read_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewOwnedSubscriptionAsync("Home insurance", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        var property = await app.NewPropertyAsync("Private house", Visibility.Private, (app.AdultA, 10_000));
        (await app.RawAsync(app.AdultA, HttpMethod.Put, InsuranceUrl(id), Home(property.Value, "Fire"))).EnsureSuccessStatusCode();
        var history = $"/api/v1/platform/audit/items/{id.Value}";

        var forA = await app.GetRawAsync(app.AdultA, history);
        var forB = await app.GetRawAsync(app.AdultB, history);

        Assert.Contains(property.Value.ToString(), forA);
        Assert.DoesNotContain(property.Value.ToString(), forB);
    }

    [Fact]
    public async Task Co_owner_who_cannot_read_the_property_sees_no_link()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewOwnedSubscriptionAsync("Home insurance", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        var property = await app.NewPropertyAsync("Private house", Visibility.Private, (app.AdultA, 10_000));
        (await app.RawAsync(app.AdultA, HttpMethod.Put, InsuranceUrl(id), Home(property.Value, "Fire"))).EnsureSuccessStatusCode();

        var response = await app.RawAsync(app.AdultB, HttpMethod.Get, Url(id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(property.Value.ToString(), text);
        Assert.DoesNotContain("Private house", text);
        var insurance = JsonDocument.Parse(text).RootElement.GetProperty("insurance");
        Assert.Equal(JsonValueKind.Null, insurance.GetProperty("property").ValueKind);
        Assert.Equal("home", insurance.GetProperty("kind").GetString());
        Assert.Equal("HX-123", insurance.GetProperty("policyNumber").GetString());
        Assert.Equal(["Fire"], insurance.GetProperty("guarantees").EnumerateArray().Select(g => g.GetProperty("label").GetString()));
    }

    [Fact]
    public async Task Deleted_property_shows_no_link()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);
        var property = await app.NewPropertyAsync("Maison", Visibility.Shared, (app.AdultA, 10_000));
        (await app.RawAsync(app.AdultA, HttpMethod.Put, InsuranceUrl(id), Home(property.Value, "Fire"))).EnsureSuccessStatusCode();
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            await core.Items.Where(i => i.Id == property.Value).ExecuteDeleteAsync();

        var insurance = await InsuranceOf(app, app.AdultA, id);

        Assert.Equal(JsonValueKind.Null, insurance.GetProperty("property").ValueKind);
        Assert.Equal("home", insurance.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Bad_kind_is_refused_at_kind()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, InsuranceUrl(id), new { kind = "boat", guarantees = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = Assert.Single((await Body(response)).GetProperty("errors").EnumerateArray());
        Assert.Equal("kind", error.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Reader_without_write_gets_the_usual_refusal()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m); // shared, owned by A alone: B reads, A writes

        var put = await app.RawAsync(app.AdultB, HttpMethod.Put, InsuranceUrl(id), Home(null, "Fire"));
        var delete = await app.RawAsync(app.AdultB, HttpMethod.Delete, InsuranceUrl(id));

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task A_blank_guarantee_label_is_refused_at_its_indexed_path()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, InsuranceUrl(id),
            new { kind = "home", guarantees = new[] { new { label = "Fire" }, new { label = " " } } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = Assert.Single((await Body(response)).GetProperty("errors").EnumerateArray());
        Assert.Equal(("guarantees[1].label", "subscriptions.guarantee_label"), (error.GetProperty("path").GetString(), error.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task An_over_long_text_is_refused_at_its_member()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewMonthlyAsync(app.AdultA, 10m);

        var policy = await app.RawAsync(app.AdultA, HttpMethod.Put, InsuranceUrl(id), new { kind = "home", policyNumber = new string('x', 201) });
        var ceiling = await app.RawAsync(app.AdultA, HttpMethod.Put, InsuranceUrl(id),
            new { kind = "home", guarantees = new[] { new { label = "Fire", ceiling = new string('x', 201) } } });

        var paths = new List<string?>();
        foreach (var response in new[] { policy, ceiling })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            paths.Add(Assert.Single((await Body(response)).GetProperty("errors").EnumerateArray()).GetProperty("path").GetString());
        }
        Assert.Equal(["policyNumber", "guarantees[0].ceiling"], paths);
    }
}
