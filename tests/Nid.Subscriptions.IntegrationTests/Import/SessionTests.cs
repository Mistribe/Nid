using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Nid.Platform.Contracts;
using Nid.Platform.Import;
using Nid.TestSupport;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Subscriptions.IntegrationTests.Import;

[Collection("postgres")]
public class SessionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_session_starts_open_and_lives_24_hours()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);

        var session = await client.StartAsync(app.AdultA);

        Assert.Equal("open", session.State);
        Assert.Equal(TimeSpan.FromHours(24), session.ExpiresAt - session.CreatedAt);
        Assert.Equal("open", (await client.GetAsync(app.AdultA, session.Id)).State);
    }

    [Fact]
    public async Task A_second_session_waits_for_the_first_but_the_other_adult_is_not_blocked()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        await client.StartAsync(app.AdultA);

        var second = await client.StartRawAsync(app.AdultA);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(ImportCodes.SessionActive, await ImportClient.CodeOf(second));

        await client.StartAsync(app.AdultB);
    }

    [Fact]
    public async Task Another_persons_session_does_not_exist_for_them()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);

        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{ImportClient.Root}/{session.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{ImportClient.Root}/{session.Id}")).StatusCode);
        Assert.Empty(await app.GetAsync<List<SessionResponse>>(app.AdultB, ImportClient.Root));
        Assert.Single(await app.GetAsync<List<SessionResponse>>(app.AdultA, ImportClient.Root));
    }

    [Theory]
    [InlineData("nid.backup", 1)]
    [InlineData("nid.data", 2)]
    public async Task Only_nid_data_version_1_is_accepted(string format, int version)
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await new ImportClient(app).StartRawAsync(app.AdultA, format: format, version: version);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ImportCodes.UnsupportedFormat, await ImportClient.CodeOf(response));
    }

    [Fact]
    public async Task A_cancelled_session_frees_the_person()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);

        var cancelled = await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{ImportClient.Root}/{session.Id}");

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("cancelled", (await client.GetAsync(app.AdultA, session.Id)).State);
        await client.StartAsync(app.AdultA);
    }

    [Fact]
    public async Task An_expired_session_is_cleared_when_the_next_one_starts()
    {
        // NidApp's clock is frozen, so a session can only be past its expiry if it was born past it.
        await using var app = await NidApp.StartAsync(fixture, services =>
            services.AddSingleton(new ImportOptions { SessionLifetime = TimeSpan.FromSeconds(-1) }));
        var client = new ImportClient(app);
        var stale = await client.StartAsync(app.AdultA);

        await client.StartAsync(app.AdultA);

        Assert.Equal("expired", (await client.GetAsync(app.AdultA, stale.Id)).State);
    }

    /// <summary>
    /// The lifetime holds on use, not only when a sweep happens to run: a session past its expiry reads expired
    /// and refuses every action, though nothing has marked it so in the database yet.
    /// </summary>
    [Fact]
    public async Task A_session_past_its_expiry_is_expired_for_every_call()
    {
        await using var app = await NidApp.StartAsync(fixture, services =>
            services.AddSingleton(new ImportOptions { SessionLifetime = TimeSpan.FromSeconds(-1) }));
        var client = new ImportClient(app);
        var stale = await client.StartAsync(app.AdultA);
        var url = $"{ImportClient.Root}/{stale.Id}";

        Assert.Equal("expired", (await client.GetAsync(app.AdultA, stale.Id)).State);
        foreach (var response in new[]
                 {
                     await client.PutRawAsync(app.AdultA, stale.Id, "persons", 0, """[{"ref":"adult-1","kind":"adult","display_name":"A"}]"""),
                     await client.ValidateRawAsync(app.AdultA, stale.Id),
                     await client.CommitRawAsync(app.AdultA, stale.Id),
                     await app.RawAsync(app.AdultA, HttpMethod.Delete, url),
                 })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(ImportCodes.State, await ImportClient.CodeOf(response));
            Assert.Contains("expired", await response.Content.ReadAsStringAsync());
        }

        Assert.Equal("expired", (await client.GetAsync(app.AdultA, stale.Id)).State);
    }

    /// <summary>
    /// The 1C1 hazard: a request Synapse's generator did not see runs with no behavior at all. These two
    /// refusals can only come from ActorBehavior and ScopeBehavior wrapping Platform's own requests.
    /// </summary>
    [Fact]
    public async Task Import_requests_run_behind_the_actor_and_scope_behaviors()
    {
        await using var app = await NidApp.StartAsync(fixture);

        await using (var anonymous = app.Services.CreateAsyncScope())
        {
            var result = await anonymous.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new ListImportsQuery());
            Assert.True(result.TryGetFailure(out var failure));
            Assert.IsType<UnauthenticatedFailure>(failure);
        }

        await using (var writeOnly = app.Services.CreateAsyncScope())
        {
            writeOnly.ServiceProvider.GetRequiredService<IActorAccessor>().Current =
                new Actor(ActorKind.Person, app.AdultA, ActorOrigin.Api, new HashSet<string> { ScopeNames.Write });
            var result = await writeOnly.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new ListImportsQuery());
            Assert.True(result.TryGetFailure(out var failure));
            Assert.IsType<UnauthorizedFailure>(failure);
        }
    }
}
