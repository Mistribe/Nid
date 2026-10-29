using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Host.Auth;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Audit;

/// <summary>1F3a spec §5: the events of the OIDC callback and of sign-out; the groups of the identity provider decide access and the admin role.</summary>
public class SignInEventTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly string[] Member = ["nid-users"];
    private static readonly string[] Administrator = ["nid-users", "nid-admins"];

    private static Task<UnambitiousFx.Functional.Result<PersonInfo>> SignInAsync(
        NidApp app, string subject, string name, string? email, params string[] groups)
        => SignInInScopeAsync(app, subject, name, email, groups);

    private static async Task<UnambitiousFx.Functional.Result<PersonInfo>> SignInInScopeAsync(
        NidApp app, string subject, string name, string? email, string[] groups)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OidcSignIn>().CompleteAsync(subject, name, email, groups, default);
    }

    [Fact]
    public async Task A_sign_in_is_recorded_for_the_person()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var result = await SignInAsync(app, "sub-a", "Alice", null, Administrator);

        Assert.True(result.TryGetValue(out var person));
        var login = Assert.Single(await AuditApi.EventsAsync(app, "login"));
        Assert.Equal((person.Id.Value, person.Id.Value, "person", "ui"), (login.PersonId!.Value, login.SubjectPersonId!.Value, login.ActorKind, login.Via));
        // Alice already was an admin and still is: the role did not change.
        Assert.Empty(await AuditApi.EventsAsync(app, "admin_granted"));
        Assert.Empty(await AuditApi.EventsAsync(app, "admin_revoked"));
    }

    [Fact]
    public async Task A_sign_in_outside_the_access_group_is_refused_with_the_subject_and_the_reason_only()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var result = await SignInAsync(app, "sub-b", "Bob Renamed", "bob@example.test");

        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal(PersonCodes.NotInAccessGroup, failure!.Code);
        var refused = Assert.Single(await AuditApi.EventsAsync(app, "login_refused"));
        Assert.Null(refused.SubjectPersonId);
        var data = JsonDocument.Parse(refused.Data).RootElement;
        Assert.Equal("sub-b", data.GetProperty("subject").GetString());
        Assert.Equal(PersonCodes.NotInAccessGroup, data.GetProperty("reason").GetString());
        Assert.DoesNotContain("bob@example.test", refused.Data);
        Assert.DoesNotContain("Bob Renamed", refused.Data);
        Assert.Empty(await AuditApi.EventsAsync(app, "login"));
        // Nothing was refreshed for a refused identity.
        await using var db = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.DoesNotContain(db.Persons, p => p.DisplayName == "Bob Renamed");
    }

    [Fact]
    public async Task A_new_adult_is_created_when_the_household_has_room()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RaiseCapAsync(app, 3);

        var result = await SignInAsync(app, "sub-new", "Carol", "carol@example.test", Member);

        Assert.True(result.TryGetValue(out var carol));
        Assert.Equal((PersonKind.Adult, false, "carol@example.test"), (carol.Kind, carol.IsAdmin, carol.Email));
        var login = Assert.Single(await AuditApi.EventsAsync(app, "login"));
        Assert.Equal(carol.Id.Value, login.SubjectPersonId!.Value);
    }

    [Fact]
    public async Task A_new_adult_beyond_the_cap_is_refused_and_recorded()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var result = await SignInAsync(app, "sub-mallory", "Mallory Name", "mallory@example.test", Member);

        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal(PersonCodes.AdultCap, failure!.Code);
        var refused = Assert.Single(await AuditApi.EventsAsync(app, "login_refused"));
        Assert.Equal(PersonCodes.AdultCap, JsonDocument.Parse(refused.Data).RootElement.GetProperty("reason").GetString());
        Assert.DoesNotContain("mallory@example.test", refused.Data);
    }

    [Fact]
    public async Task An_email_another_adult_holds_is_refused_and_recorded()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await RaiseCapAsync(app, 3);
        await using (var db = PostgresFixture.NewCoreContext(app.ConnectionString))
            await db.Persons.Where(p => p.Id == app.AdultB.Value).ExecuteUpdateAsync(s => s.SetProperty(p => p.Email, "bob@example.test"));

        var result = await SignInAsync(app, "sub-bob-again", "Bob", "BOB@example.test", Member);

        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal(PersonCodes.EmailTaken, failure!.Code);
        Assert.Equal(PersonCodes.EmailTaken, JsonDocument.Parse(Assert.Single(await AuditApi.EventsAsync(app, "login_refused")).Data)
            .RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Joining_the_admin_group_makes_an_adult_an_admin_and_is_recorded()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var result = await SignInAsync(app, "sub-b", "Bob", null, Administrator);

        Assert.True(result.TryGetValue(out var bob));
        Assert.True(bob.IsAdmin);
        var granted = Assert.Single(await AuditApi.EventsAsync(app, "admin_granted"));
        Assert.Equal(app.AdultB.Value, granted.SubjectPersonId!.Value);
        Assert.Equal("identity_provider", JsonDocument.Parse(granted.Data).RootElement.GetProperty("source").GetString());
        await using var db = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.True((await db.Persons.SingleAsync(p => p.Id == app.AdultB.Value)).IsAdmin);
    }

    [Fact]
    public async Task Leaving_the_admin_group_ends_the_role_at_the_next_sign_in_and_is_recorded()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var result = await SignInAsync(app, "sub-a", "Alice", null, Member);

        Assert.True(result.TryGetValue(out var alice));
        Assert.False(alice.IsAdmin);
        var revoked = Assert.Single(await AuditApi.EventsAsync(app, "admin_revoked"));
        Assert.Equal(app.AdultA.Value, revoked.SubjectPersonId!.Value);
        await using var db = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.False((await db.Persons.SingleAsync(p => p.Id == app.AdultA.Value)).IsAdmin);
    }

    [Fact]
    public async Task A_sign_out_is_recorded_for_the_person()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultB, HttpMethod.Post, "/auth/logout");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var logout = Assert.Single(await AuditApi.EventsAsync(app, "logout"));
        Assert.Equal((app.AdultB.Value, app.AdultB.Value), (logout.PersonId!.Value, logout.SubjectPersonId!.Value));
    }

    /// <summary>The harness seeds two adults and no household row, so the cap is the default 2: a row says otherwise.</summary>
    private static async Task RaiseCapAsync(NidApp app, int cap)
    {
        await using var db = PostgresFixture.NewCoreContext(app.ConnectionString);
        db.HouseholdSettings.Add(new HouseholdSettingsRow { MaxAdults = cap });
        await db.SaveChangesAsync();
    }
}
