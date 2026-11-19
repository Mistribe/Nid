using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Contracts;
using Nid.Platform.Admin;
using Nid.TestSupport;

namespace Nid.Banking.IntegrationTests;

/// <summary>Issue #343: Administration › Banque says why bank sync is not configured, and tests the id and the key against the fake Enable Banking.</summary>
public class DiagnosticsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Diagnostics = "/api/v1/admin/banking/diagnostics";
    private const string Test = "/api/v1/admin/banking/test";

    /// <summary>A host whose secrets folder is empty, so the key is only what <paramref name="key"/> says.</summary>
    private static async Task<NidApp> StartAsync(PostgresFixture fixture, string? key)
    {
        var settings = new Dictionary<string, string> { ["Secrets:Path"] = Directory.CreateTempSubdirectory("nid-secrets-").FullName };
        if (key is not null)
            settings["EnableBanking:PrivateKey"] = key;
        var app = await NidApp.StartAsync(fixture, settings: settings);
        app.Banks.FailWith = null;
        return app;
    }

    private static string RsaPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    private static bool? OptionalBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetBoolean() : null;

    private static string? OptionalString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    [Fact]
    public async Task Diagnostics_say_which_part_is_missing_to_the_administrator_only()
    {
        await using var app = await StartAsync(fixture, null);

        var diagnostics = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, Diagnostics)).RootElement;
        Assert.Equal((BankingKeyStatus.Missing, false, BankingApi.Callback),
            (diagnostics.GetProperty("keyStatus").GetString(), diagnostics.GetProperty("appIdSaved").GetBoolean(), diagnostics.GetProperty("redirectUrl").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, Diagnostics)).StatusCode);

        await BankingApi.SettingsAsync(app, new { bankingEnabled = true, enableBankingAppId = "app-1" });
        var admin = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"{BankingApi.Root}/status")).RootElement;
        Assert.Equal((false, true, BankingKeyStatus.Missing),
            (admin.GetProperty("configured").GetBoolean(), OptionalBool(admin, "appIdSaved"), OptionalString(admin, "keyStatus")));
        // The other adult only learns it is not configured.
        var adult = JsonDocument.Parse(await app.GetRawAsync(app.AdultB, $"{BankingApi.Root}/status")).RootElement;
        Assert.Equal((false, null, null), (adult.GetProperty("configured").GetBoolean(), OptionalBool(adult, "appIdSaved"), OptionalString(adult, "keyStatus")));
    }

    [Fact]
    public async Task Diagnostics_never_return_the_key()
    {
        var pem = RsaPem();
        await using var app = await StartAsync(fixture, pem);
        await BankingApi.SettingsAsync(app, new { enableBankingAppId = "app-1" });

        var body = await app.GetRawAsync(app.AdultA, Diagnostics);

        Assert.Equal(BankingKeyStatus.Loaded, JsonDocument.Parse(body).RootElement.GetProperty("keyStatus").GetString());
        Assert.DoesNotContain("PRIVATE KEY", body, StringComparison.Ordinal);
        Assert.DoesNotContain(pem.Split('\n')[1], body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_test_reports_an_accepted_application_its_activation_and_its_redirect_urls()
    {
        await using var app = await StartAsync(fixture, RsaPem());
        await BankingApi.SettingsAsync(app, new { enableBankingAppId = "app-1" });

        app.Banks.Application = new EnableBankingApplication(false, ["https://elsewhere.example/callback", BankingApi.Callback]);
        var accepted = await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Test));
        Assert.Equal(("accepted", false, true), (accepted.GetProperty("outcome").GetString(), OptionalBool(accepted, "active"), OptionalBool(accepted, "redirectUrlAllowed")));

        app.Banks.Application = new EnableBankingApplication(true, ["https://elsewhere.example/callback"]);
        var mismatch = await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Test));
        Assert.Equal((true, false), (OptionalBool(mismatch, "active"), OptionalBool(mismatch, "redirectUrlAllowed")));

        Assert.Equal(["app-1", "app-1"], app.Banks.ApplicationCalls);
    }

    [Fact]
    public async Task The_test_reports_a_refusal_with_its_code_and_an_unreachable_api()
    {
        await using var app = await StartAsync(fixture, RsaPem());
        await BankingApi.SettingsAsync(app, new { enableBankingAppId = "app-1" });

        var rejected = await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Test));
        Assert.Equal(("rejected", "enablebanking.UNAUTHORIZED_ACCESS", null),
            (rejected.GetProperty("outcome").GetString(), OptionalString(rejected, "code"), OptionalBool(rejected, "active")));

        app.Banks.FailWith = BankingCodes.ProviderError;
        var unreachable = await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Test));
        Assert.Equal(("unreachable", null), (unreachable.GetProperty("outcome").GetString(), OptionalString(unreachable, "code")));
    }

    private sealed class Scheduled(DateTimeOffset next) : IJobStatusReader
    {
        public Task<IReadOnlyList<JobSchedule>> ListAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<JobSchedule>>([new("fx-daily", "0 0 6 * * ?", next.AddHours(2)), new("bank-sync-nightly", "0 30 4 * * ?", next)]);
    }

    [Fact]
    public async Task Diagnostics_give_the_last_run_the_next_one_and_whether_a_bank_is_linked()
    {
        var next = new DateTimeOffset(2025, 10, 16, 4, 30, 0, TimeSpan.Zero);
        await using var app = await BankingApi.StartAsync(fixture, overrides: s => s.AddSingleton<IJobStatusReader>(new Scheduled(next)));

        var before = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, Diagnostics)).RootElement;
        Assert.Equal((JsonValueKind.Null, next, false),
            (before.GetProperty("lastRun").ValueKind, before.GetProperty("nextRunAt").GetDateTimeOffset(), before.GetProperty("bankLinked").GetBoolean()));

        var connection = await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-1", "hash-1", null, "Courant B"));
        (await app.RawAsync(app.AdultB, HttpMethod.Post, $"{BankingApi.Root}/sync", new { connectionId = connection })).EnsureSuccessStatusCode();

        var after = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, Diagnostics)).RootElement;
        Assert.True(after.GetProperty("bankLinked").GetBoolean());
        Assert.Equal("manual", after.GetProperty("lastRun").GetProperty("trigger").GetString());
    }

    [Fact]
    public async Task The_test_needs_an_id_and_a_usable_key_and_an_administrator()
    {
        await using (var keyless = await StartAsync(fixture, null))
        {
            await BankingApi.SettingsAsync(keyless, new { enableBankingAppId = "app-1" });
            await BankingApi.AssertRefusedAsync(await keyless.RawAsync(keyless.AdultA, HttpMethod.Post, Test), HttpStatusCode.Conflict, BankingCodes.NotConfigured);
        }

        await using var app = await StartAsync(fixture, "not a key");
        await BankingApi.SettingsAsync(app, new { enableBankingAppId = "app-1" });
        Assert.Equal(BankingKeyStatus.Invalid, JsonDocument.Parse(await app.GetRawAsync(app.AdultA, Diagnostics)).RootElement.GetProperty("keyStatus").GetString());
        await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Test), HttpStatusCode.Conflict, BankingCodes.NotConfigured);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Post, Test)).StatusCode);
        Assert.Empty(app.Banks.ApplicationCalls);
    }
}
