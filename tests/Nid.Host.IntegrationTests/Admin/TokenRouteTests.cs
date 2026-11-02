using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Host.IntegrationTests.Audit;
using Nid.TestSupport;
using static Nid.TestSupport.TokenApi;

namespace Nid.Host.IntegrationTests.Admin;

/// <summary>Spec 4A §6: « Mes jetons » through a browser session.</summary>
public class TokenRouteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_token_is_created_listed_without_its_secret_and_revoked()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var created = await CreateAsync(app, app.AdultA);

        Assert.StartsWith($"nid_pat_{created.Token.Prefix}_", created.Secret);
        Assert.Equal(("propose", "active"), (created.Token.Preset, created.Token.Status));
        Assert.Equal(new DateTimeOffset(2026, 1, 13, 0, 0, 0, TimeSpan.Zero), created.Token.ExpiresAt); // 2025-10-15 + 90 days
        var listed = await app.GetRawAsync(app.AdultA, Tokens);
        Assert.Contains(created.Token.Id.ToString(), listed);
        Assert.DoesNotContain(created.Secret, listed);

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Tokens}/{created.Token.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Tokens}/{created.Token.Id}")).StatusCode);
        var after = Assert.Single(await app.GetAsync<List<Token>>(app.AdultA, Tokens));
        Assert.Equal("revoked", after.Status);

        var events = await AuditApi.EventsAsync(app, SecurityEventNames.TokenCreated);
        Assert.Equal("propose", JsonDocument.Parse(Assert.Single(events).Data).RootElement.GetProperty("preset").GetString());
        Assert.Single(await AuditApi.EventsAsync(app, SecurityEventNames.TokenRevoked)); // the second DELETE changed nothing
    }

    [Fact]
    public async Task A_person_never_sees_nor_revokes_another_persons_tokens()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var alices = await CreateAsync(app, app.AdultA);

        Assert.Empty(await app.GetAsync<List<Token>>(app.AdultB, Tokens));
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{Tokens}/{alices.Token.Id}")).StatusCode);
        Assert.Equal("active", Assert.Single(await app.GetAsync<List<Token>>(app.AdultA, Tokens)).Status);
    }

    [Fact]
    public async Task Invalid_fields_answer_422_at_their_path_and_the_eleventh_token_409()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var invalid = await app.RawAsync(app.AdultA, HttpMethod.Post, Tokens, new { name = "", preset = "propose" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var problem = await invalid.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("name", problem.GetProperty("errors")[0].GetProperty("path").GetString());

        foreach (var (body, path) in new (object, string)[] { (new { }, "name"), (new { name = "x" }, "preset") })
        {
            var missing = await app.RawAsync(app.AdultA, HttpMethod.Post, Tokens, body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
            Assert.Equal(path, (await missing.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken: TestContext.Current.CancellationToken)).GetProperty("errors")[0].GetProperty("path").GetString());
        }

        for (var i = 0; i < 10; i++)
            await CreateAsync(app, app.AdultA);
        var eleventh = await app.RawAsync(app.AdultA, HttpMethod.Post, Tokens, new { name = "x", preset = "read" });
        Assert.Equal(HttpStatusCode.Conflict, eleventh.StatusCode);
        Assert.Equal(TokenCodes.LimitReached, (await eleventh.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken: TestContext.Current.CancellationToken)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Signing_out_everywhere_revokes_the_tokens_only_when_asked()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await CreateAsync(app, app.AdultA);

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/core/me/revoke-sessions")).StatusCode);
        Assert.Equal("active", Assert.Single(await app.GetAsync<List<Token>>(app.AdultA, Tokens)).Status);
    }

    [Fact]
    public async Task Signing_out_everywhere_with_revoke_tokens_revokes_them_all_and_says_how_many()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await CreateAsync(app, app.AdultA);
        await CreateAsync(app, app.AdultA);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/core/me/revoke-sessions", new { revokeTokens = true });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        // The session was revoked too: read the rows back directly rather than through the (now stale) session.
        var revoked = Assert.Single(await AuditApi.EventsAsync(app, SecurityEventNames.TokenRevoked));
        Assert.Equal("2", JsonDocument.Parse(revoked.Data).RootElement.GetProperty("count").GetString());
        Assert.Single(await AuditApi.EventsAsync(app, SecurityEventNames.SessionRevoked));
    }
}
