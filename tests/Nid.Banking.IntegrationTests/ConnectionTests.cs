using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Contracts;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Core.Contracts;
using Nid.TestSupport;
using NodaTime;
using Duration = NodaTime.Duration;

namespace Nid.Banking.IntegrationTests;

/// <summary>Bank sync spec §3.1, §3.3 and §8: connecting a bank, the callback's state, renewal, a joint account, deletion.</summary>
public class ConnectionTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string JointIban = "FR76 3000 6000 0112 3456 7890 189";

    private sealed class SettableClock(Instant now) : IClock
    {
        public Instant Now { get; set; } = now;

        public Instant GetCurrentInstant() => Now;
    }

    private static BankingDbContext Db(NidApp app)
        => new(new DbContextOptionsBuilder<BankingDbContext>().Configure(app.ConnectionString).Options);

    [Fact]
    public async Task Start_returns_the_bank_url_and_stores_only_the_state_hash()
    {
        await using var app = await BankingApi.StartAsync(fixture);

        var (id, state) = await BankingApi.StartConnectionAsync(app, app.AdultA);

        var call = Assert.Single(app.Banks.StartAuths);
        Assert.Equal(("app-1", BankingApi.Alpha, state, BankingApi.Callback), (call.AppId, call.Aspsp, call.State, call.RedirectUrl));
        Assert.Equal(BankingApi.Now + Duration.FromDays(90), call.ValidUntil); // the bank's maximum consent
        Assert.Equal(43, state.Length); // 32 random bytes, base64url without padding
        await using var db = Db(app);
        var row = await db.Connections.SingleAsync(c => c.Id == id);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(state)), row.StateHash);
        Assert.Equal(("pending", app.AdultA.Value, "Banque Alpha", "FR"), (row.Status, row.PersonId, row.AspspName, row.AspspCountry));
        // Pending until the callback: not listed.
        Assert.Equal(0, (await BankingApi.ConnectionsAsync(app, app.AdultA)).GetArrayLength());

        // A bank without a maximum consent is asked for 180 days.
        await BankingApi.StartConnectionAsync(app, app.AdultA, "Banque Beta");
        Assert.Equal(BankingApi.Now + Duration.FromDays(180), app.Banks.StartAuths[1].ValidUntil);
    }

    [Fact]
    public async Task Abandoned_pending_connections_are_purged_when_the_person_starts_another()
    {
        var clock = new SettableClock(BankingApi.Now);
        await using var app = await BankingApi.StartAsync(fixture, overrides: s => s.AddSingleton<IClock>(clock));
        var (abandoned, _) = await BankingApi.StartConnectionAsync(app, app.AdultA);
        var (others, _) = await BankingApi.StartConnectionAsync(app, app.AdultB);

        clock.Now += Duration.FromMinutes(16);
        var (fresh, _) = await BankingApi.StartConnectionAsync(app, app.AdultA);

        await using var db = Db(app);
        var left = await db.Connections.Select(c => c.Id).ToListAsync();
        Assert.DoesNotContain(abandoned, left);
        Assert.Contains(others, left); // another person's are theirs to purge
        Assert.Contains(fresh, left);
    }

    [Fact]
    public async Task Disabled_or_unconfigured_refuses_409()
    {
        await using (var off = await BankingApi.StartAsync(fixture, enable: false))
        {
            Assert.Equal((false, false), await StatusAsync(off));
            await BankingApi.AssertRefusedAsync(await off.RawAsync(off.AdultA, HttpMethod.Get, $"{BankingApi.Root}/aspsps"), HttpStatusCode.Conflict, BankingCodes.Disabled);
            await BankingApi.AssertRefusedAsync(await off.RawAsync(off.AdultA, HttpMethod.Post, $"{BankingApi.Root}/connections", new { aspspName = "Banque Alpha" }),
                HttpStatusCode.Conflict, BankingCodes.Disabled);
            await BankingApi.AssertRefusedAsync(await off.RawAsync(off.AdultB, HttpMethod.Get, $"{BankingApi.Root}/accounts"), HttpStatusCode.Conflict, BankingCodes.Disabled);

            await BankingApi.SettingsAsync(off, new { bankingEnabled = true });
            Assert.Equal((true, false), await StatusAsync(off));
            await BankingApi.AssertRefusedAsync(await off.RawAsync(off.AdultA, HttpMethod.Get, $"{BankingApi.Root}/connections"), HttpStatusCode.Conflict, BankingCodes.NotConfigured);
        }

        // An application id without the private key is not configured either.
        await using var keyless = await NidApp.StartAsync(fixture);
        await BankingApi.SettingsAsync(keyless, new { bankingEnabled = true, enableBankingAppId = "app-1" });
        Assert.Equal((true, false), await StatusAsync(keyless));
        await BankingApi.AssertRefusedAsync(await keyless.RawAsync(keyless.AdultA, HttpMethod.Get, $"{BankingApi.Root}/aspsps"), HttpStatusCode.Conflict, BankingCodes.NotConfigured);

        await using var on = await BankingApi.StartAsync(fixture);
        Assert.Equal((true, true), await StatusAsync(on));
        var banks = JsonDocument.Parse(await on.GetRawAsync(on.AdultB, $"{BankingApi.Root}/aspsps")).RootElement;
        Assert.Equal([("Banque Alpha", "https://logo.example/alpha.png"), ("Banque Beta", null)],
            banks.EnumerateArray().Select(b => (b.GetProperty("name").GetString(), b.GetProperty("logo").GetString())));
        await on.GetRawAsync(on.AdultA, $"{BankingApi.Root}/aspsps");
        Assert.Equal(["FR"], on.Banks.Countries); // a day's cache

        static async Task<(bool, bool)> StatusAsync(NidApp app)
        {
            var status = JsonDocument.Parse(await app.GetRawAsync(app.AdultB, $"{BankingApi.Root}/status")).RootElement;
            return (status.GetProperty("enabled").GetBoolean(), status.GetProperty("configured").GetBoolean());
        }
    }

    [Fact]
    public async Task Unknown_aspsp_is_422()
    {
        await using var app = await BankingApi.StartAsync(fixture);

        foreach (var name in new[] { "Banque Inconnue", "Bank Elsewhere" })
            await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/connections", new { aspspName = name }),
                HttpStatusCode.UnprocessableEntity, BankingCodes.UnknownAspsp);
        Assert.Empty(app.Banks.StartAuths);
    }

    [Fact]
    public async Task Callback_completes_the_connection_and_lists_its_accounts()
    {
        await using var app = await BankingApi.StartAsync(fixture);

        var id = await BankingApi.ConnectAsync(app, app.AdultA, "code-1",
            BankingApi.Account("uid-1", "hash-1", JointIban, "Compte joint"),
            BankingApi.Account("uid-2", "hash-2", "FR1420041010050500013M02606", "Livret A", "SVGS"),
            BankingApi.Account("uid-3", "hash-3", null, "Carte", "CARD"),
            // A currency that is no ISO code: the account is left out, the others are kept.
            BankingApi.Account("uid-4", "hash-4", null, "Compte bizarre", "CACC", "EURO"));

        Assert.Equal(["code-1"], app.Banks.CodesExchanged);
        var connection = Assert.Single((await BankingApi.ConnectionsAsync(app, app.AdultA)).EnumerateArray());
        Assert.Equal((id, "Banque Alpha", "active"), (connection.GetProperty("id").GetGuid(), connection.GetProperty("aspspName").GetString(), connection.GetProperty("status").GetString()));
        Assert.Equal(BankingApi.ValidUntil.ToDateTimeOffset(), connection.GetProperty("validUntil").GetDateTimeOffset());
        Assert.Equal(
            [("Carte", null, "card", "EUR", true), ("Compte joint", "0189", "checking", "EUR", true), ("Livret A", "2606", "savings", "EUR", true)],
            connection.GetProperty("accounts").EnumerateArray().Select(a => (
                a.GetProperty("name").GetString(), a.GetProperty("ibanMasked").GetString(), a.GetProperty("kind").GetString(),
                a.GetProperty("currency").GetString(), a.GetProperty("synced").GetBoolean())));
        Assert.All(connection.GetProperty("accounts").EnumerateArray(), a => Assert.True(a.GetProperty("canWrite").GetBoolean()));

        // The session id is kept encrypted, and nowhere in what the person reads.
        await using var db = Db(app);
        Assert.DoesNotContain("session-code-1", Encoding.UTF8.GetString((await db.Connections.SingleAsync(c => c.Id == id)).SessionId!));
        Assert.DoesNotContain("session-code-1", await app.GetRawAsync(app.AdultA, $"{BankingApi.Root}/connections"));
        // B never sees A's connections.
        Assert.Equal(0, (await BankingApi.ConnectionsAsync(app, app.AdultB)).GetArrayLength());
    }

    [Fact]
    public async Task Callback_state_is_single_use_personal_and_expires()
    {
        var clock = new SettableClock(BankingApi.Now);
        await using var app = await BankingApi.StartAsync(fixture, overrides: s => s.AddSingleton<IClock>(clock));
        app.Banks.Sessions["code"] = new AuthorizedSession("session-x", BankingApi.ValidUntil, [BankingApi.Account("uid-1", "hash-1", JointIban, "Compte")]);
        const string invalid = "/settings/banks?error=" + BankingCodes.StateInvalid;

        // Replayed: the first callback used the state.
        var (_, state) = await BankingApi.StartConnectionAsync(app, app.AdultA);
        Assert.Equal(HttpStatusCode.Redirect, (await BankingApi.CallbackAsync(app, app.AdultA, $"state={state}&code=code")).StatusCode);
        Assert.Equal(invalid, (await BankingApi.CallbackAsync(app, app.AdultA, $"state={state}&code=code")).Headers.Location!.OriginalString);
        Assert.Single(app.Banks.CodesExchanged);

        // Opened in B's browser: refused, no session created.
        (_, state) = await BankingApi.StartConnectionAsync(app, app.AdultA);
        Assert.Equal(invalid, (await BankingApi.CallbackAsync(app, app.AdultB, $"state={state}&code=code")).Headers.Location!.OriginalString);
        Assert.Single(app.Banks.CodesExchanged);

        // Back after more than 15 minutes.
        (_, state) = await BankingApi.StartConnectionAsync(app, app.AdultA);
        clock.Now += Duration.FromMinutes(16);
        Assert.Equal(invalid, (await BankingApi.CallbackAsync(app, app.AdultA, $"state={state}&code=code")).Headers.Location!.OriginalString);

        // Unknown.
        Assert.Equal(invalid, (await BankingApi.CallbackAsync(app, app.AdultA, "state=nope&code=code")).Headers.Location!.OriginalString);

        // The person said no at the bank.
        (_, state) = await BankingApi.StartConnectionAsync(app, app.AdultA);
        Assert.Equal("/settings/banks?error=" + BankingCodes.AuthRefused,
            (await BankingApi.CallbackAsync(app, app.AdultA, $"state={state}&error=access_denied")).Headers.Location!.OriginalString);

        Assert.Single(app.Banks.CodesExchanged);
        Assert.Single((await BankingApi.ConnectionsAsync(app, app.AdultA)).EnumerateArray());
    }

    [Fact]
    public async Task Renewal_keeps_accounts_by_identification_hash()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Private);
        var id = await BankingApi.ConnectAsync(app, app.AdultA, "code-1",
            BankingApi.Account("uid-1", "hash-1", null, "Compte"), BankingApi.Account("uid-2", "hash-2", null, "Livret", "SVGS"));
        var accounts = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{accounts[1]}",
            new { envelopeId = envelope.Value, autoApplyBalance = true })).StatusCode);
        await using (var db = Db(app))
            await db.Connections.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "expired"));

        var renew = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/connections/{id}/renew");
        Assert.Equal(HttpStatusCode.OK, renew.StatusCode);
        var renewed = await BankingApi.JsonAsync(renew);
        Assert.Equal(id, renewed.GetProperty("id").GetGuid());
        // Still listed while the person is at the bank.
        Assert.Equal("expired", (await BankingApi.ConnectionsAsync(app, app.AdultA))[0].GetProperty("status").GetString());
        var validUntil = Instant.FromUtc(2026, 4, 1, 0, 0);
        app.Banks.Sessions["code-2"] = new AuthorizedSession("session-code-2", validUntil,
            [BankingApi.Account("uid-1b", "hash-1", null, "Compte"), BankingApi.Account("uid-2b", "hash-2", null, "Livret", "SVGS")]);
        var callback = await BankingApi.CallbackAsync(app, app.AdultA, $"state={BankingApi.StateOf(renewed.GetProperty("redirectUrl").GetString()!)}&code=code-2");
        Assert.Equal($"/settings/banks?connected={id}", callback.Headers.Location!.OriginalString);

        var connection = Assert.Single((await BankingApi.ConnectionsAsync(app, app.AdultA)).EnumerateArray());
        Assert.Equal(("active", validUntil.ToDateTimeOffset()), (connection.GetProperty("status").GetString(), connection.GetProperty("validUntil").GetDateTimeOffset()));
        var after = connection.GetProperty("accounts").EnumerateArray().ToList();
        Assert.Equal(accounts, after.Select(a => a.GetProperty("id").GetGuid()));
        Assert.Equal((envelope.Value, true), (after[1].GetProperty("envelopeId").GetGuid(), after[1].GetProperty("autoApplyBalance").GetBoolean()));
        await using (var db = Db(app))
            Assert.Equal(["uid-1b", "uid-2b"], await db.Sources.Where(s => s.ConnectionId == id).OrderBy(s => s.IdentificationHash).Select(s => s.ProviderUid).ToListAsync());
        // The superseded consent is revoked at the bank.
        Assert.Equal(["session-code-1"], app.Banks.DeletedSessions);
    }

    [Fact]
    public async Task Second_connection_attaches_to_the_same_account()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var first = await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-a", "hash-a", JointIban, "Compte joint"));

        var second = await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-b", "hash-b", JointIban.Replace(" ", ""), "CPT JOINT"));

        var ofA = Assert.Single((await BankingApi.ConnectionsAsync(app, app.AdultA))[0].GetProperty("accounts").EnumerateArray()).GetProperty("id").GetGuid();
        var ofB = Assert.Single((await BankingApi.ConnectionsAsync(app, app.AdultB))[0].GetProperty("accounts").EnumerateArray()).GetProperty("id").GetGuid();
        Assert.Equal(ofA, ofB);
        await using var db = Db(app);
        Assert.Equal(1, await db.Accounts.CountAsync());
        Assert.Equal([first, second], (await db.Sources.Where(s => s.AccountId == ofA).Select(s => s.ConnectionId).ToListAsync()).Order());
    }

    [Fact]
    public async Task Delete_revokes_the_session_and_keeps_the_accounts()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var id = await BankingApi.ConnectAsync(app, app.AdultA, "code-1", BankingApi.Account("uid-1", "hash-1", JointIban, "Compte"));
        var account = (await BankingApi.AccountsAsync(app, app.AdultA))[0].GetProperty("id").GetGuid();
        var envelope = await app.NewEnvelopeAsync("Compte", app.AdultA, Visibility.Private);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{account}", new { envelopeId = envelope.Value })).StatusCode);

        await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultB, HttpMethod.Delete, $"{BankingApi.Root}/connections/{id}"),
            HttpStatusCode.NotFound, BankingCodes.ConnectionNotFound);
        Assert.Empty(app.Banks.DeletedSessions);

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{BankingApi.Root}/connections/{id}")).StatusCode);

        Assert.Equal(["session-code-1"], app.Banks.DeletedSessions);
        Assert.Equal(0, (await BankingApi.ConnectionsAsync(app, app.AdultA)).GetArrayLength());
        await using var db = Db(app);
        Assert.False(await db.Sources.AnyAsync(s => s.ConnectionId == id));
        // Kept, no longer synchronised.
        var kept = Assert.Single((await BankingApi.AccountsAsync(app, app.AdultA, envelope)).EnumerateArray());
        Assert.Equal((account, false), (kept.GetProperty("id").GetGuid(), kept.GetProperty("synced").GetBoolean()));
        await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/connections/{id}/renew"),
            HttpStatusCode.NotFound, BankingCodes.ConnectionNotFound);
        // Still its own: taken off the envelope, which another bank account may then take.
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{account}", new { clearEnvelope = true })).StatusCode);
        Assert.True(Assert.Single((await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray()).GetProperty("canWrite").GetBoolean());
    }

    [Theory]
    [InlineData("enablebanking.HTTP_404")]
    [InlineData("enablebanking.SESSION_DOES_NOT_EXIST")]
    public async Task Delete_takes_a_session_already_gone_at_the_bank_as_revoked(string answer)
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var id = await BankingApi.ConnectAsync(app, app.AdultA, "code-1", BankingApi.Account("uid-1", "hash-1", null, "Compte"));
        app.Banks.FailWith = answer;

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{BankingApi.Root}/connections/{id}")).StatusCode);

        Assert.Equal(0, (await BankingApi.ConnectionsAsync(app, app.AdultA)).GetArrayLength());
    }
}
