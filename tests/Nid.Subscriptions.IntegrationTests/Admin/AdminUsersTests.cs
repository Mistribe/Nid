using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Subscriptions.IntegrationTests.Admin;

public class AdminUsersTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Users = "/api/v1/admin/users";
    private const string Household = "/api/v1/admin/household";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Adult(Guid Id, string DisplayName, string? Email, string Status, bool IsAdmin);
    public sealed record Settings(int MaxAdults, string ReferenceCurrency, int AdultCount);

    private static async Task<string?> CodeOf(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("code").GetString();

    /// <summary>What the OIDC callback does at a first sign-in (AuthenticationSetup.ProvisionAndReplacePrincipal).</summary>
    private static async Task<bool> SignInAsync(NidApp app, string subject, string name, string? email, bool isAdmin = false)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var result = await scope.ServiceProvider.GetRequiredService<IInvoker>()
            .InvokeAsync(new ProvisionAdultCommand(subject, name, email, isAdmin));
        return result.IsSuccess;
    }

    [Fact]
    public async Task The_admin_lists_the_adults_but_not_the_dependent()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var adults = await app.GetAsync<List<Adult>>(app.AdultA, Users);

        Assert.Equal(2, adults.Count);
        Assert.Contains(adults, a => a.Id == app.AdultA.Value && a.IsAdmin && a.Status == "active");
        Assert.Contains(adults, a => a.Id == app.AdultB.Value && !a.IsAdmin);
        Assert.DoesNotContain(adults, a => a.Id == app.Dependent.Value);
    }

    [Fact]
    public async Task Every_admin_route_refuses_an_adult_who_is_not_admin()
    {
        await using var app = await NidApp.StartAsync(fixture);

        foreach (var (method, url, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, Users, null),
                     (HttpMethod.Post, Users, new { displayName = "Carol", email = "carol@example.test" }),
                     (HttpMethod.Delete, $"{Users}/{app.AdultA.Value}", null),
                     (HttpMethod.Post, $"{Users}/{app.AdultA.Value}/revoke-sessions", null),
                     (HttpMethod.Get, Household, null),
                     (HttpMethod.Patch, Household, new { maxAdults = 5 }),
                 })
        {
            var response = await app.RawAsync(app.AdultB, method, url, body);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {url} answered {response.StatusCode}");
            Assert.Equal(AdminCodes.Required, await CodeOf(response));
        }
    }

    [Fact]
    public async Task A_preregistered_adult_signs_in_by_email_once_the_cap_allows_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var carol = new { displayName = "Carol", email = "carol@example.test" };

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Post, Users, carol);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, PersonCodes.AdultCap), (refused.StatusCode, await CodeOf(refused)));

        (await app.RawAsync(app.AdultA, HttpMethod.Patch, Household, new { maxAdults = 3 })).EnsureSuccessStatusCode();
        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, Users, carol);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var pending = (await created.Content.ReadFromJsonAsync<Adult>(Json))!;
        Assert.Equal(("pending", false), (pending.Status, pending.IsAdmin));

        Assert.False(await SignInAsync(app, "sub-x", "Mallory", "mallory@example.test"));
        Assert.True(await SignInAsync(app, "sub-c", "Carol C.", "CAROL@Example.test"));

        var adults = await app.GetAsync<List<Adult>>(app.AdultA, Users);
        Assert.Contains(adults, a => a.Id == pending.Id && a.Status == "active" && a.DisplayName == "Carol C.");
    }

    [Fact]
    public async Task A_pending_adult_is_an_admin_once_bound_when_the_identity_provider_says_so()
    {
        await using var app = await NidApp.StartAsync(fixture);
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, Household, new { maxAdults = 3 })).EnsureSuccessStatusCode();
        var pending = (await (await app.RawAsync(app.AdultA, HttpMethod.Post, Users,
            new { displayName = "Carol", email = "carol@example.test" })).Content.ReadFromJsonAsync<Adult>(Json))!;

        Assert.True(await SignInAsync(app, "sub-c", "Carol C.", "carol@example.test", isAdmin: true));

        var adults = await app.GetAsync<List<Adult>>(app.AdultA, Users);
        Assert.Contains(adults, a => a.Id == pending.Id && a.Status == "active" && a.IsAdmin);
    }

    [Fact]
    public async Task An_admin_can_sign_itself_out_everywhere()
    {
        await using var app = await NidApp.StartAsync(fixture);

        // Self "sign out everywhere": the last unsafe request this test sends as A, since its CSRF token
        // (bound to A's session epoch) is stale for A once this succeeds.
        var revoked = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Users}/{app.AdultA.Value}/revoke-sessions");

        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Equal(1, await core.Persons.Where(p => p.Id == app.AdultA.Value).Select(p => p.SessionEpoch).SingleAsync());
    }

    [Fact]
    public async Task Only_a_pending_adult_can_be_removed_and_an_unknown_one_is_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, Household, new { maxAdults = 3 })).EnsureSuccessStatusCode();
        var pending = (await (await app.RawAsync(app.AdultA, HttpMethod.Post, Users,
            new { displayName = "Carol", email = "carol@example.test" })).Content.ReadFromJsonAsync<Adult>(Json))!;

        var active = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Users}/{app.AdultB.Value}");
        Assert.Equal((HttpStatusCode.UnprocessableEntity, PersonCodes.PersonActive), (active.StatusCode, await CodeOf(active)));

        var removed = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Users}/{pending.Id}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(pending.Id, (await removed.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid());

        var unknown = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Users}/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_demotion_at_sign_in_takes_effect_at_the_next_request_of_every_session()
    {
        await using var app = await NidApp.StartAsync(fixture);

        // The identity provider took Alice out of the admin group and put Bob in it; both signed in again.
        Assert.True(await SignInAsync(app, "sub-b", "Bob", null, isAdmin: true));
        Assert.True(await SignInAsync(app, "sub-a", "Alice", null, isAdmin: false));

        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultA, HttpMethod.Get, Users)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Get, Users)).StatusCode);
    }

    [Fact]
    public async Task Signing_another_adult_out_everywhere_increments_their_epoch()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var revoked = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Users}/{app.AdultB.Value}/revoke-sessions");

        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Equal(1, await core.Persons.Where(p => p.Id == app.AdultB.Value).Select(p => p.SessionEpoch).SingleAsync());
    }

    [Fact]
    public async Task The_household_settings_are_read_and_changed_field_by_field()
    {
        await using var app = await NidApp.StartAsync(fixture);

        Assert.Equal(new Settings(2, "EUR", 2), await app.GetAsync<Settings>(app.AdultA, Household));

        var below = await app.RawAsync(app.AdultA, HttpMethod.Patch, Household, new { maxAdults = 1 });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, PersonCodes.CapBelowCount), (below.StatusCode, await CodeOf(below)));
        var badCurrency = await app.RawAsync(app.AdultA, HttpMethod.Patch, Household, new { referenceCurrency = "euro" });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, PersonCodes.UnknownCurrency), (badCurrency.StatusCode, await CodeOf(badCurrency)));
        var unknownKey = await app.RawTextAsync(app.AdultA, HttpMethod.Patch, Household, """{"timezone":"UTC"}""");
        Assert.Equal(HttpStatusCode.BadRequest, unknownKey.StatusCode);

        (await app.RawAsync(app.AdultA, HttpMethod.Patch, Household, new { referenceCurrency = "USD" })).EnsureSuccessStatusCode();
        Assert.Equal(new Settings(2, "USD", 2), await app.GetAsync<Settings>(app.AdultA, Household));
    }
}
