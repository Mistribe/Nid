using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.TestSupport;

/// <summary>Shared by the banking tests and the host-wide suites: a host with banking on, two French banks and a private key; connections made through the real routes.</summary>
public static class BankingApi
{
    public const string Root = "/api/v1/banking";
    public const string Callback = "https://test.localhost/api/v1/banking/callback";

    public static readonly Aspsp Alpha = new("Banque Alpha", "FR", "https://logo.example/alpha.png", 90 * 24 * 3600);
    public static readonly Aspsp Beta = new("Banque Beta", "FR", null, null);

    public static readonly Instant Now = NidApp.GoldenCaseToday.AtMidnight().InUtc().ToInstant();
    public static readonly Instant ValidUntil = Instant.FromUtc(2026, 1, 13, 0, 0);

    public static async Task<NidApp> StartAsync(PostgresFixture fixture, bool enable = true, Action<IServiceCollection>? overrides = null)
    {
        var app = await NidApp.StartAsync(fixture, overrides, new Dictionary<string, string> { ["EnableBanking:PrivateKey"] = "test-key" });
        app.Banks.FailWith = null;
        app.Banks.Aspsps.AddRange([Alpha, Beta, new Aspsp("Bank Elsewhere", "DE", null, null)]);
        if (enable)
            await SettingsAsync(app, new { bankingEnabled = true, enableBankingAppId = "app-1" });
        return app;
    }

    public static async Task SettingsAsync(NidApp app, object body)
        => (await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/admin/household", body)).EnsureSuccessStatusCode();

    public static SessionAccount Account(string uid, string hash, string? iban, string name, string type = "CACC", string currency = "EUR")
        => new(uid, hash, iban, name, type, currency);

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    /// <summary>Starts a connection at <paramref name="bank"/> and returns its id and the state the bank will send back.</summary>
    public static async Task<(Guid Id, string State)> StartConnectionAsync(NidApp app, PersonId person, string bank = "Banque Alpha")
    {
        var response = await app.RawAsync(person, HttpMethod.Post, $"{Root}/connections", new { aspspName = bank });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var started = await JsonAsync(response);
        return (started.GetProperty("id").GetGuid(), StateOf(started.GetProperty("redirectUrl").GetString()!));
    }

    public static string StateOf(string redirectUrl) => redirectUrl[(redirectUrl.IndexOf("state=", StringComparison.Ordinal) + "state=".Length)..];

    public static Task<HttpResponseMessage> CallbackAsync(NidApp app, PersonId person, string query)
        => app.GetUnfollowedAsync(person, $"{Root}/callback?{query}");

    /// <summary>A whole connection: started, then the bank's callback with a code whose session lists <paramref name="accounts"/>.</summary>
    public static async Task<Guid> ConnectAsync(NidApp app, PersonId person, string code, params SessionAccount[] accounts)
    {
        app.Banks.Sessions[code] = new AuthorizedSession("session-" + code, ValidUntil, accounts);
        var (id, state) = await StartConnectionAsync(app, person);
        var callback = await CallbackAsync(app, person, $"state={state}&code={code}");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal($"/settings/banks?connected={id}", callback.Headers.Location!.OriginalString);
        return id;
    }

    public static async Task<JsonElement> ConnectionsAsync(NidApp app, PersonId person)
        => JsonDocument.Parse(await app.GetRawAsync(person, $"{Root}/connections")).RootElement;

    public static async Task<JsonElement> AccountsAsync(NidApp app, PersonId person, ItemId? envelope = null)
    {
        var response = await app.RawAsync(person, HttpMethod.Get, $"{Root}/accounts" + (envelope is { } e ? $"?envelopeId={e.Value}" : ""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }
}
