using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Contracts;
using Nid.Platform.Admin;
using Nid.TestSupport;

namespace Nid.Banking.IntegrationTests;

/// <summary>
/// Administration › Banque: where bank sync stands (#344), the adults with their own application (#382); « Tester la connexion » with an
/// adult's own application against the fake Enable Banking (#414: there is no installation application to test any more).
/// </summary>
public class DiagnosticsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Diagnostics = "/api/v1/admin/banking/diagnostics";
    private const string Test = $"{BankingApi.Root}/application/test";

    private static bool? OptionalBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetBoolean() : null;

    private static string? OptionalString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    [Fact]
    public async Task Diagnostics_are_the_administrator_s_and_name_the_adults_with_their_own_application()
    {
        await using var app = await BankingApi.StartAsync(fixture, enable: false);
        await BankingApi.ApplicationAsync(app, app.AdultB);

        var diagnostics = JsonElement.Parse(await app.GetRawAsync(app.AdultA, Diagnostics));
        Assert.Equal([app.AdultB.Value], diagnostics.GetProperty("ownApplications").EnumerateArray().Select(a => a.GetProperty("personId").GetGuid()));
        Assert.False(diagnostics.TryGetProperty("keyStatus", out _));
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Get, Diagnostics)).StatusCode);
    }

    [Fact]
    public async Task The_status_says_whether_the_person_has_an_application_and_gives_the_redirect_url()
    {
        await using var app = await BankingApi.StartAsync(fixture, enable: false);
        await BankingApi.SettingsAsync(app, new { bankingEnabled = true });
        await BankingApi.ApplicationAsync(app, app.AdultB);

        var alice = JsonElement.Parse(await app.GetRawAsync(app.AdultA, $"{BankingApi.Root}/status"));
        var bob = JsonElement.Parse(await app.GetRawAsync(app.AdultB, $"{BankingApi.Root}/status"));
        Assert.Equal((false, BankingApi.Callback), (alice.GetProperty("configured").GetBoolean(), OptionalString(alice, "redirectUrl")));
        Assert.True(bob.GetProperty("configured").GetBoolean());
        Assert.False(alice.TryGetProperty("appIdSaved", out _));
    }

    [Fact]
    public async Task The_test_reports_an_accepted_application_its_activation_and_its_redirect_urls()
    {
        await using var app = await BankingApi.StartAsync(fixture);

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
        await using var app = await BankingApi.StartAsync(fixture);

        var rejected = await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Test));
        Assert.Equal(("rejected", "enablebanking.UNAUTHORIZED_ACCESS", null),
            (rejected.GetProperty("outcome").GetString(), OptionalString(rejected, "code"), OptionalBool(rejected, "active")));

        app.Banks.FailWith = BankingCodes.ProviderError;
        var unreachable = await BankingApi.JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Test));
        Assert.Equal(("unreachable", null), (unreachable.GetProperty("outcome").GetString(), OptionalString(unreachable, "code")));
    }

    [Fact]
    public async Task There_is_no_installation_test_or_transfer_any_more()
    {
        await using var app = await BankingApi.StartAsync(fixture);

        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/admin/banking/test")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/admin/banking/transfer", new { personId = app.AdultB.Value })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/admin/household", new { enableBankingAppId = "app-1" })).StatusCode);
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

}
