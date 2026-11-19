using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Host.Admin;
using Nid.Host.Auth;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Admin;

/// <summary>
/// Issue 215, local-accounts.md §6: with `Auth:AdminSource=nid` the admin area and the CLI decide the admin role; the last
/// admin keeps it; an OIDC sign-in in `both` leaves it alone. With the identity provider as the source, Nid refuses.
/// </summary>
public class AdminRoleTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Dictionary<string, string> NidDecides = new() { ["Auth:Mode"] = "local" };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> SetAsync(NidApp app, PersonId actor, PersonId target, bool admin)
        => app.RawAsync(actor, HttpMethod.Put, $"/api/v1/admin/users/{target.Value}/admin", new { admin });

    private static async Task<bool> IsAdminAsync(NidApp app, PersonId id)
    {
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        return (await core.Persons.AsNoTracking().SingleAsync(p => p.Id == id.Value, Ct)).IsAdmin;
    }

    [Fact]
    public async Task An_admin_gives_the_role_then_takes_it_back_and_each_change_is_recorded()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: NidDecides);

        var granted = await SetAsync(app, app.AdultA, app.AdultB, true);
        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        Assert.True((await granted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("isAdmin").GetBoolean());
        Assert.True(await IsAdminAsync(app, app.AdultB));
        // Bob is admin at once: the next request reads the role from the database.
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Get, "/api/v1/admin/users")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SetAsync(app, app.AdultA, app.AdultB, false)).StatusCode);
        Assert.False(await IsAdminAsync(app, app.AdultB));
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, "/api/v1/admin/users")).StatusCode);

        var grant = Assert.Single(await AuditApi.EventsAsync(app, SecurityEventNames.AdminGranted));
        Assert.Equal("nid", JsonSerializer.Deserialize<Dictionary<string, string>>(grant.Data)!["source"]);
        Assert.Equal(app.AdultA.Value, grant.PersonId);
        Assert.Single(await AuditApi.EventsAsync(app, SecurityEventNames.AdminRevoked));
    }

    [Fact]
    public async Task The_last_admin_keeps_the_role_even_against_two_demotions_at_once()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: NidDecides);
        var refused = await SetAsync(app, app.AdultA, app.AdultA, false);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(PersonCodes.LastAdmin, (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.OK, (await SetAsync(app, app.AdultA, app.AdultB, true)).StatusCode);
        var both = await Task.WhenAll(SetAsync(app, app.AdultA, app.AdultA, false), SetAsync(app, app.AdultB, app.AdultB, false));

        Assert.Single(both, r => r.StatusCode == HttpStatusCode.OK);
        Assert.True(await IsAdminAsync(app, app.AdultA) || await IsAdminAsync(app, app.AdultB));
    }

    [Fact]
    public async Task A_dependent_is_never_admin_and_a_change_that_changes_nothing_records_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: NidDecides);

        Assert.Equal(HttpStatusCode.NotFound, (await SetAsync(app, app.AdultA, app.Dependent, true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(app, app.AdultA, app.AdultA, true)).StatusCode);
        Assert.Empty(await AuditApi.EventsAsync(app, SecurityEventNames.AdminGranted));
    }

    [Fact]
    public async Task With_the_identity_provider_as_the_source_Nid_does_not_edit_the_role()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var refused = await SetAsync(app, app.AdultA, app.AdultB, true);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(PersonCodes.AdminSourceIdentityProvider, (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
        Assert.False(await IsAdminAsync(app, app.AdultB));
    }

    [Fact]
    public async Task In_both_modes_an_oidc_sign_in_keeps_the_role_nid_decided()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: new Dictionary<string, string> { ["Auth:Mode"] = "both" });
        await using (var scope = app.Services.CreateAsyncScope())
        {
            // Alice (sub-a) is admin and the identity provider no longer says so; Bob (sub-b) is not and it says he is.
            var signIn = scope.ServiceProvider.GetRequiredService<OidcSignIn>();
            Assert.True((await signIn.CompleteAsync("sub-a", "Alice", null, ["nid-users"], Ct)).IsSuccess);
            Assert.True((await signIn.CompleteAsync("sub-b", "Bob", null, ["nid-users", "nid-admins"], Ct)).IsSuccess);
        }

        Assert.True(await IsAdminAsync(app, app.AdultA));
        Assert.False(await IsAdminAsync(app, app.AdultB));
        Assert.Empty(await AuditApi.EventsAsync(app, SecurityEventNames.AdminRevoked));
        Assert.Empty(await AuditApi.EventsAsync(app, SecurityEventNames.AdminGranted));
    }

    [Fact]
    public async Task The_cli_grants_and_revokes_the_role_and_keeps_the_last_admin()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: NidDecides);

        var granted = await CliAsync(app, "grant-admin", "--person", app.AdultB.Value.ToString());
        Assert.Equal(0, granted.Exit);
        Assert.Contains("est maintenant administrateur", granted.Out);
        Assert.Equal(0, (await CliAsync(app, "revoke-admin", "--person", app.AdultA.Value.ToString())).Exit);
        var last = await CliAsync(app, "revoke-admin", "--person", app.AdultB.Value.ToString());

        Assert.Equal(1, last.Exit);
        Assert.Contains("dernier administrateur", last.Error);
        Assert.True(await IsAdminAsync(app, app.AdultB));
        var source = JsonSerializer.Deserialize<Dictionary<string, string>>((await AuditApi.EventsAsync(app, SecurityEventNames.AdminGranted)).Single().Data)!;
        Assert.Equal("admin-cli", source["source"]);
    }

    [Fact]
    public async Task The_cli_refuses_when_the_identity_provider_decides()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var run = await CliAsync(app, "grant-admin", "--person", app.AdultB.Value.ToString());

        Assert.Equal(1, run.Exit);
        Assert.Contains("fournisseur d'identité", run.Error);
    }

    private static async Task<(int Exit, string Out, string Error)> CliAsync(NidApp app, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await AdminCli.RunAsync(app.Services, args, new CliConsole(new StringReader(""), output, error, true));
        return (exit, output.ToString(), error.ToString());
    }
}
