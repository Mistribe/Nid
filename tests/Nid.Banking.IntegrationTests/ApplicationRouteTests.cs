using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Banking.IntegrationTests;

/// <summary>#382: an adult declares, replaces, tests and removes their own Enable Banking application.</summary>
public class ApplicationRouteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Route = $"{BankingApi.Root}/application";

    private static string Pem() => RSA.Create(2048).ExportRSAPrivateKeyPem();

    private static Task<HttpResponseMessage> PutAsync(NidApp app, PersonId person, string id, string? key)
        => app.RawAsync(person, HttpMethod.Put, Route, new { applicationId = id, privateKey = key });

    private static async Task AssertFieldAsync(HttpResponseMessage response, string code, string field)
    {
        await BankingApi.AssertRefusedAsync(response, HttpStatusCode.UnprocessableEntity, code);
        var errors = (await BankingApi.JsonAsync(response)).GetProperty("errors").EnumerateArray().Select(e => e.FieldPath()).ToList();
        Assert.Contains(field, errors);
    }

    [Fact]
    public async Task Saving_and_deleting_leave_security_events_without_the_key_or_the_id()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await PutAsync(app, app.AdultA, "app-alice", Pem());
        await PutAsync(app, app.AdultA, "app-alice", null);
        await PutAsync(app, app.AdultA, "app-alice", Pem());
        await app.RawAsync(app.AdultA, HttpMethod.Delete, Route);

        var saved = await AuditApi.EventsAsync(app, SecurityEventNames.BankingApplicationSaved);
        Assert.Equal(["true", "false", "true"], saved.Select(e => JsonDocument.Parse(e.Data).RootElement.GetProperty("key_replaced").GetString()));
        var deleted = await AuditApi.EventsAsync(app, SecurityEventNames.BankingApplicationDeleted);
        Assert.Single(deleted);
        foreach (var e in saved.Concat(deleted))
        {
            Assert.DoesNotContain("app-alice", e.Data);
            Assert.DoesNotContain("PRIVATE KEY", e.Data);
        }
    }

    [Fact]
    public async Task A_body_over_the_cap_is_refused()
    {
        await using var app = await BankingApi.StartAsync(fixture);

        var response = await app.RawContentAsync(app.AdultA, HttpMethod.Put, Route, new StringContent(
            JsonSerializer.Serialize(new { applicationId = "app-alice", privateKey = Pem() }) + new string(' ', 40 * 1024), System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Get, Route)).StatusCode);
    }

    [Fact]
    public async Task Alice_declares_her_application_and_the_key_never_comes_back()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var pem = Pem();
        var body = pem.Split('\n')[1];

        var put = await PutAsync(app, app.AdultA, " app-alice ", pem);
        var text = await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.DoesNotContain("PRIVATE KEY", text);
        Assert.DoesNotContain(body, text);

        var get = await app.GetRawAsync(app.AdultA, Route);
        Assert.DoesNotContain(body, get);
        var view = JsonDocument.Parse(get).RootElement;
        Assert.Equal(("app-alice", "loaded", false), (view.GetProperty("applicationId").GetString(), view.GetProperty("keyStatus").GetString(), view.GetProperty("inUse").GetBoolean()));

        // Bob has none.
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultB, HttpMethod.Get, Route)).StatusCode);
    }

    [Fact]
    public async Task A_public_key_is_refused_and_nothing_is_stored()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        using var rsa = RSA.Create(2048);

        var pem = rsa.ExportRSAPublicKeyPem();
        var refused = await PutAsync(app, app.AdultA, "app-alice", pem);
        await BankingApi.AssertRefusedAsync(refused, HttpStatusCode.UnprocessableEntity, BankingCodes.KeyInvalid);
        Assert.DoesNotContain(pem.Split('\n')[1], await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Get, Route)).StatusCode);
    }

    [Fact]
    public async Task Creating_without_a_key_or_an_id_is_refused()
    {
        await using var app = await BankingApi.StartAsync(fixture);

        await AssertFieldAsync(await PutAsync(app, app.AdultA, "app-alice", null), BankingCodes.KeyRequired, "privateKey");
        await AssertFieldAsync(await PutAsync(app, app.AdultA, "  ", Pem()), BankingCodes.ApplicationIdInvalid, "applicationId");
        await AssertFieldAsync(await PutAsync(app, app.AdultA, new string('x', 101), Pem()), BankingCodes.ApplicationIdInvalid, "applicationId");
    }

    [Fact]
    public async Task While_a_connection_uses_it_the_id_stays_but_the_key_can_change()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(app, app.AdultA, "app-alice", Pem())).StatusCode);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant"));

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(app, app.AdultA, "app-alice", Pem())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(app, app.AdultA, "app-alice", null)).StatusCode);
        await BankingApi.AssertRefusedAsync(await PutAsync(app, app.AdultA, "app-other", null), HttpStatusCode.Conflict, BankingCodes.ApplicationInUse);
        await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Delete, Route), HttpStatusCode.Conflict, BankingCodes.ApplicationInUse);
        Assert.True(JsonDocument.Parse(await app.GetRawAsync(app.AdultA, Route)).RootElement.GetProperty("inUse").GetBoolean());
    }

    [Fact]
    public async Task Deleting_removes_the_pending_connections_on_it()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await PutAsync(app, app.AdultA, "app-alice", Pem());
        var (_, state) = await BankingApi.StartConnectionAsync(app, app.AdultA);

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, Route)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Get, Route)).StatusCode);
        var callback = await BankingApi.CallbackAsync(app, app.AdultA, $"state={state}&code=x");
        Assert.Contains("error=", callback.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_test_uses_the_actor_credential()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var test = $"{Route}/test";
        await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, test), HttpStatusCode.Conflict, BankingCodes.NotConfigured);

        await PutAsync(app, app.AdultA, "app-alice", Pem());
        app.Banks.Application = new EnableBankingApplication(true, [BankingApi.Callback]);
        var answer = JsonDocument.Parse(await (await app.RawAsync(app.AdultA, HttpMethod.Post, test)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        Assert.Equal(("accepted", true), (answer.GetProperty("outcome").GetString(), answer.GetProperty("redirectUrlAllowed").GetBoolean()));
        Assert.Equal(["app-alice"], app.Banks.ApplicationCalls);
    }

    [Fact]
    public async Task Diagnostics_list_who_has_an_application_without_its_id()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await PutAsync(app, app.AdultA, "app-alice", Pem());

        var raw = await app.GetRawAsync(app.AdultA, "/api/v1/admin/banking/diagnostics");

        Assert.DoesNotContain("app-alice", raw);
        var own = JsonDocument.Parse(raw).RootElement.GetProperty("ownApplications");
        Assert.Equal(app.AdultA.Value, Assert.Single(own.EnumerateArray()).GetProperty("personId").GetGuid());
    }
}
