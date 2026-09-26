using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Host.Auth;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Audit;

/// <summary>1F3a spec §5: the events of the OIDC callback and of sign-out.</summary>
[Collection("postgres")]
public class SignInEventTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_sign_in_is_recorded_for_the_person()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<OidcSignIn>().CompleteAsync("sub-a", "Alice", null, default);

        Assert.True(result.TryGetValue(out var person));
        var login = Assert.Single(await SecurityEventTests.EventsAsync(app, "login"));
        Assert.Equal((person.Id.Value, person.Id.Value, "person", "ui"), (login.PersonId!.Value, login.SubjectPersonId!.Value, login.ActorKind, login.Via));
    }

    [Fact]
    public async Task A_refused_sign_in_keeps_the_subject_and_nothing_else()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<OidcSignIn>()
            .CompleteAsync("sub-mallory", "Mallory Name", "mallory@example.test", default);

        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal(PersonCodes.NotRegistered, failure!.Code);
        var refused = Assert.Single(await SecurityEventTests.EventsAsync(app, "login_refused"));
        Assert.Null(refused.SubjectPersonId);
        Assert.Equal("sub-mallory", JsonDocument.Parse(refused.Data).RootElement.GetProperty("subject").GetString());
        Assert.DoesNotContain("mallory@example.test", refused.Data);
        Assert.DoesNotContain("Mallory Name", refused.Data);
    }

    [Fact]
    public async Task A_sign_out_is_recorded_for_the_person()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultB, HttpMethod.Post, "/auth/logout");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var logout = Assert.Single(await SecurityEventTests.EventsAsync(app, "logout"));
        Assert.Equal((app.AdultB.Value, app.AdultB.Value), (logout.PersonId!.Value, logout.SubjectPersonId!.Value));
    }
}
