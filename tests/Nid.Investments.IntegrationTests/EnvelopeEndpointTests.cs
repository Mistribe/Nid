using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Investments.IntegrationTests;

public class EnvelopeEndpointTests(PostgresFixture fixture, SharedNidApp shared) : IClassFixture<PostgresFixture>, IClassFixture<SharedNidApp>
{
    private static object Create(NidApp app, string name = "PEA", object? opening = null, object? firstValuation = null, string visibility = "shared")
        => new
        {
            displayName = name, kind = "pea", currency = "EUR", visibility,
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            institution = "Banque", accountHint = "4521", valuationCadence = "monthly",
            opening, firstValuation,
        };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Creating_with_an_opening_and_a_first_value_shows_the_gain()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", Create(app,
            opening: new { occurredOn = "2025-01-31", amount = new { amount = "20000.00", currency = "EUR" } },
            firstValuation: new { asOf = "2025-01-31", amount = new { amount = "21000.00", currency = "EUR" } }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();

        var detail = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/investments/envelopes/{id}"));
        Assert.Equal("PEA", detail.GetProperty("displayName").GetString());
        Assert.Equal("pea", detail.GetProperty("kind").GetString());
        var figures = detail.GetProperty("figures");
        Assert.Equal("21000.0000", figures.GetProperty("value").GetProperty("amount").GetString());
        Assert.Equal("1000.0000", figures.GetProperty("gain").GetProperty("amount").GetString());
        Assert.Equal(5.0m, figures.GetProperty("gainPct").GetDecimal());
        Assert.Single(detail.GetProperty("valuations").EnumerateArray());
        Assert.Single(detail.GetProperty("flows").EnumerateArray());
    }

    [Fact]
    public async Task The_list_names_the_owners_so_two_envelopes_of_the_same_name_can_be_told_apart()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var ofA = await app.NewEnvelopeAsync("Livret A", app.AdultA, Visibility.Shared);
        var ofB = await app.NewEnvelopeAsync("Livret A", app.AdultB, Visibility.Shared);

        var list = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/investments/envelopes?scope=household"));

        var owners = list.GetProperty("items").EnumerateArray().ToDictionary(
            i => i.GetProperty("id").GetGuid(),
            i => i.GetProperty("owners").EnumerateArray().Select(o => (o.GetProperty("personId").GetGuid(), o.GetProperty("shareBp").GetInt32())).ToList());
        Assert.Equal([(app.AdultA.Value, 10_000)], owners[ofA.Value]);
        Assert.Equal([(app.AdultB.Value, 10_000)], owners[ofB.Value]);
    }

    [Fact]
    public async Task A_refused_first_value_leaves_no_envelope_behind()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", Create(app,
            firstValuation: new { asOf = "2025-10-16", amount = new { amount = "1.00", currency = "EUR" } }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("core.date_in_future", (await JsonAsync(refused)).GetProperty("code").GetString());
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Equal(0, await core.Items.CountAsync(i => i.Module == "investments", cancellationToken: TestContext.Current.CancellationToken));
        await using var investments = PostgresFixture.NewInvestmentsContext(app.ConnectionString);
        Assert.Equal(0, await investments.Envelopes.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("kind", "casino", "investments.unknown_kind")]
    [InlineData("valuationCadence", "hourly", "investments.unknown_cadence")]
    [InlineData("accountHint", "12345", "investments.invalid_envelope")]
    public async Task A_bad_attribute_is_refused(string field, string value, string code)
    {
        var app = shared.App;
        var body = JsonSerializer.SerializeToElement(Create(app));
        var patched = JsonSerializer.Deserialize<Dictionary<string, object?>>(body.GetRawText())!;
        patched[field] = value;

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", patched);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(code, (await JsonAsync(refused)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_or_blank_name_is_refused(string name)
    {
        var app = shared.App;

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", Create(app, name: name));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("investments.invalid_envelope", (await JsonAsync(refused)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_name_over_100_characters_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", Create(app, name: new string('a', 101)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("investments.invalid_envelope", (await JsonAsync(refused)).GetProperty("code").GetString());
    }

    /// <summary>
    /// Fix round 2: System.Text.Json binds a missing or explicit-null JSON field to a real <c>null</c> even for
    /// a non-nullable <c>string</c> record parameter (no required-parameter enforcement is configured) — a
    /// crash waiting to happen wherever a rule calls a member on that string without checking. This covers both
    /// shapes for <c>displayName</c> (omitted key, and <c>null</c> value) and the omitted shape for <c>kind</c>
    /// (which already handled null through <c>EnvelopeWire.TryKind</c>, so it is here as a confirming case, not
    /// a fix).
    /// </summary>
    [Theory]
    [InlineData("displayName", true, "investments.invalid_envelope")]
    [InlineData("displayName", false, "investments.invalid_envelope")]
    [InlineData("kind", true, "investments.unknown_kind")]
    public async Task A_missing_or_null_required_field_is_refused_not_crashed(string field, bool omit, string code)
    {
        var app = shared.App;
        var body = JsonSerializer.SerializeToElement(Create(app));
        var patched = JsonSerializer.Deserialize<Dictionary<string, object?>>(body.GetRawText())!;
        if (omit) patched.Remove(field); else patched[field] = null;

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/investments/envelopes", patched);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(code, (await JsonAsync(refused)).GetProperty("code").GetString());
        await using var investments = PostgresFixture.NewInvestmentsContext(app.ConnectionString);
        Assert.Equal(0, await investments.Envelopes.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Renaming_to_an_empty_name_is_refused_and_leaves_the_name_unchanged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"/api/v1/investments/envelopes/{id.Value}", new { displayName = "" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("investments.invalid_envelope", (await JsonAsync(refused)).GetProperty("code").GetString());
        var detail = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/investments/envelopes/{id.Value}"));
        Assert.Equal("Livret", detail.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Editing_changes_the_name_and_the_settings()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);

        var edited = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"/api/v1/investments/envelopes/{id.Value}",
            new { displayName = "Livret A", valuationCadence = "quarterly", clearAccountHint = true });

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var detail = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/investments/envelopes/{id.Value}"));
        Assert.Equal(("Livret A", "quarterly"), (detail.GetProperty("displayName").GetString(), detail.GetProperty("valuationCadence").GetString()));
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("accountHint").ValueKind);
    }

    [Fact]
    public async Task Another_adult_neither_sees_nor_reads_a_private_envelope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Secret", app.AdultA, Visibility.Private);

        var list = await JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Get, "/api/v1/investments/envelopes?scope=me"));
        Assert.Empty(list.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/investments/envelopes/{id.Value}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.RawAsync(app.AdultB, HttpMethod.Patch, $"/api/v1/investments/envelopes/{id.Value}", new { note = "x" })).StatusCode);
    }

    [Fact]
    public async Task Another_adult_reads_but_may_not_write_a_shared_envelope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret Partagé", app.AdultA, Visibility.Shared);

        var refused = await app.RawAsync(app.AdultB, HttpMethod.Patch, $"/api/v1/investments/envelopes/{id.Value}",
            new { displayName = "Hacked" });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var detail = await JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Get, $"/api/v1/investments/envelopes/{id.Value}"));
        Assert.Equal("Livret Partagé", detail.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task A_subscription_is_not_an_envelope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewSubscriptionAsync("Spotify", app.AdultA, Visibility.Shared);

        Assert.Equal(HttpStatusCode.NotFound,
            (await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/investments/envelopes/{subscription.Value}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"/api/v1/investments/envelopes/{subscription.Value}", new { note = "x" })).StatusCode);
    }

    [Fact]
    public async Task The_list_shows_shared_envelopes_in_the_household_scope_with_their_figures()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewEnvelopeAsync("Partagé", app.AdultA, Visibility.Shared);
        await app.NewEnvelopeAsync("À moi", app.AdultA, Visibility.Private);

        var household = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/investments/envelopes"));
        var me = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/investments/envelopes?scope=me"));

        Assert.Equal(["Partagé"], household.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("displayName").GetString()));
        Assert.Equal(2, me.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("items")[0].GetProperty("figures").GetProperty("value").ValueKind);
    }

    /// <summary>
    /// Minor 3: envelopes sharing a name still list in one deterministic order, tied on their id — not on
    /// whatever order the store happens to return. An edit to the first-created one (its name unchanged)
    /// forces a Postgres update, which can relocate its row away from its creation position: exactly the
    /// case a sort by name alone would leave to chance.
    /// </summary>
    [Fact]
    public async Task Envelopes_with_the_same_name_list_in_a_stable_order()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
            ids.Add((await app.NewEnvelopeAsync("Doublon", app.AdultA, Visibility.Shared)).Value);
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"/api/v1/investments/envelopes/{ids[0]}", new { valuationCadence = "quarterly" }))
            .EnsureSuccessStatusCode();
        var expected = ids.Order().ToList();

        var list = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/investments/envelopes"));

        var order = list.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(expected, order);
    }

    /// <summary>
    /// Minor 5: the archived path stays inside the same reach as every other list — adult A's closed
    /// envelopes never include adult B's closed private one, while B's own archived list does.
    /// </summary>
    [Fact]
    public async Task A_closed_private_envelope_never_appears_in_another_adults_archived_list()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await app.NewEnvelopeAsync("Secret de B", app.AdultB, Visibility.Private);
        (await app.RawAsync(app.AdultB, HttpMethod.Post, $"/api/v1/investments/envelopes/{hidden.Value}/close", new { closedOn = "2025-06-01" }))
            .EnsureSuccessStatusCode();

        var forA = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, "/api/v1/investments/envelopes?scope=me&archived=true"));
        var forB = await JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Get, "/api/v1/investments/envelopes?scope=me&archived=true"));

        Assert.DoesNotContain(forA.GetProperty("items").EnumerateArray(), e => e.GetProperty("id").GetGuid() == hidden.Value);
        Assert.Contains(forB.GetProperty("items").EnumerateArray(), e => e.GetProperty("id").GetGuid() == hidden.Value);
    }

    [Theory]
    [InlineData("life_insurance")]
    [InlineData("tontine")]
    public async Task A_life_insurance_or_a_tontine_keeps_its_premiums_before_2017_until_they_are_cleared(string kind)
    {
        await using var app = await NidApp.StartAsync(fixture);
        const string url = "/api/v1/investments/envelopes";
        var body = new
        {
            displayName = "AV", kind, currency = "EUR", visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } }, premiumsBefore2017 = "12000.50",
        };
        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, url, body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();
        async Task<(string Amount, string Currency)?> Premiums()
        {
            var premiums = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{url}/{id}"))).GetProperty("premiumsBefore2017");
            return premiums.ValueKind == JsonValueKind.Null ? null : (premiums.GetProperty("amount").GetString()!, premiums.GetProperty("currency").GetString()!);
        }

        Assert.Equal(("12000.5000", "EUR"), await Premiums());
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{url}/{id}", new { premiumsBefore2017 = "8000" })).EnsureSuccessStatusCode();
        Assert.Equal(("8000.0000", "EUR"), await Premiums());
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{url}/{id}", new { clearPremiumsBefore2017 = true })).EnsureSuccessStatusCode();
        Assert.Null(await Premiums());
    }

    [Fact]
    public async Task A_life_insurance_changed_to_another_kind_drops_its_premiums_before_2017()
    {
        await using var app = await NidApp.StartAsync(fixture);
        const string url = "/api/v1/investments/envelopes";
        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, url, new
        {
            displayName = "AV", kind = "life_insurance", currency = "EUR", visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } }, premiumsBefore2017 = "12000",
        });
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();

        var changed = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{url}/{id}", new { kind = "brokerage" });

        Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var envelope = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{url}/{id}"));
        Assert.Equal(("brokerage", JsonValueKind.Null), (envelope.GetProperty("kind").GetString(), envelope.GetProperty("premiumsBefore2017").ValueKind));
    }

    [Fact]
    public async Task Premiums_before_2017_are_refused_when_negative_or_off_a_life_insurance()
    {
        await using var app = await NidApp.StartAsync(fixture);
        const string url = "/api/v1/investments/envelopes";
        var life = await app.NewEnvelopeAsync("AV", app.AdultA, Visibility.Shared);
        var pea = await app.RawAsync(app.AdultA, HttpMethod.Post, url, new
        {
            displayName = "PEA", kind = "pea", currency = "EUR", visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } }, premiumsBefore2017 = "100",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, pea.StatusCode);
        Assert.Equal("premiumsBefore2017", (await JsonAsync(pea)).GetProperty("errors")[0].GetProperty("path").GetString());

        var other = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{url}/{life.Value}", new { premiumsBefore2017 = "100" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.StatusCode);
        var negative = await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{url}/{life.Value}", new { kind = "life_insurance", premiumsBefore2017 = "-1" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, negative.StatusCode);
        Assert.Equal("premiumsBefore2017", (await JsonAsync(negative)).GetProperty("errors")[0].GetProperty("path").GetString());
    }
}
