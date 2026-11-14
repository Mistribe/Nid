using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Host.Admin;
using Nid.Host.Auth;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using Npgsql;
using NodaTime;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>Issue 215, local-accounts.md §10, §18.1: the second factor, from its activation to `nid admin reset-totp`.</summary>
public partial class LocalAccountsTests
{
    [Fact]
    public async Task Two_step_verification_turns_on_after_a_right_code_and_then_asks_for_a_code_at_sign_in()
    {
        await using var host = await HostAsync();
        var alice = await host.SetUpAliceAsync();
        await host.EnrolAsync(alice, "Bob", "bob", BobPassword);
        var bob = await host.SignInAsync("bob", BobPassword);

        var begun = await (await bob.PostAsync("/api/v1/core/me/totp", new { })).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var secret = FromBase32(begun.GetProperty("secret").GetString()!);
        Assert.StartsWith("otpauth://totp/Nid:bob?secret=", begun.GetProperty("otpauthUri").GetString());
        // Waiting for its first code, the factor is not on: the password alone still signs in.
        Assert.Equal("done", await NextAsync(host.Browser(), "bob", BobPassword));

        var wrong = await bob.PostAsync("/api/v1/core/me/totp/confirm", new { code = "000000" == CodeNow(secret) ? "111111" : "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var step = Totp.StepAt(SystemClock.Instance.GetCurrentInstant());
        var confirmed = await bob.PostAsync("/api/v1/core/me/totp/confirm", new { code = Totp.Code(secret, step) });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var codes = (await confirmed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("codes").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Equal(10, codes.Count);
        var credential = await (await bob.GetAsync("/api/v1/core/me/credential")).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(credential.GetProperty("totpEnabled").GetBoolean());
        Assert.Equal(10, credential.GetProperty("recoveryCodesLeft").GetInt32());

        // The password opens no session any more: a code is asked.
        var phone = host.Browser();
        Assert.Equal("totp", await NextAsync(phone, "bob", BobPassword));
        Assert.False((await phone.SessionAsync()).GetProperty("authenticated").GetBoolean());
        // The code the activation used is spent: a replay.
        var replay = await phone.PostAsync("/auth/login/totp", new { code = Totp.Code(secret, step) });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(LocalSignIn.InvalidCode, (await replay.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
        // The next step's code, within the drift allowed, passes.
        var passed = await phone.PostAsync("/auth/login/totp", new { code = Totp.Code(secret, step + 1) });
        Assert.Equal(HttpStatusCode.OK, passed.StatusCode);
        Assert.True((await phone.SessionAsync()).GetProperty("authenticated").GetBoolean());

        // Used once, the same code does not open a second session.
        var other = host.Browser();
        Assert.Equal("totp", await NextAsync(other, "bob", BobPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.PostAsync("/auth/login/totp", new { code = Totp.Code(secret, step + 1) })).StatusCode);

        Assert.Single(await host.EventsAsync(SecurityEventNames.TotpEnabled));
        Assert.Contains(await host.EventsAsync(SecurityEventNames.Login),
            e => JsonSerializer.Deserialize<Dictionary<string, string>>(e.Data)!.GetValueOrDefault("method") == "password+totp");
        var users = await (await alice.GetAsync("/api/v1/admin/users")).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(users.EnumerateArray().Single(u => u.GetProperty("displayName").GetString() == "Bob").GetProperty("totpEnabled").GetBoolean());
    }

    [Fact]
    public async Task A_recovery_code_works_once_and_three_wrong_codes_send_back_to_the_password()
    {
        await using var host = await HostAsync();
        var alice = await host.SetUpAliceAsync();
        await host.EnrolAsync(alice, "Bob", "bob", BobPassword);
        var (_, codes) = await EnableTotpAsync(await host.SignInAsync("bob", BobPassword));

        var first = host.Browser();
        Assert.Equal("totp", await NextAsync(first, "bob", BobPassword));
        Assert.Equal(HttpStatusCode.OK, (await first.PostAsync("/auth/login/totp", new { recoveryCode = codes[0].ToLowerInvariant() })).StatusCode);
        var left = (await host.EventsAsync(SecurityEventNames.RecoveryCodeUsed)).Single();
        Assert.Equal("9", JsonSerializer.Deserialize<Dictionary<string, string>>(left.Data)!["left"]);

        var second = host.Browser();
        Assert.Equal("totp", await NextAsync(second, "bob", BobPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.PostAsync("/auth/login/totp", new { recoveryCode = codes[0] })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.PostAsync("/auth/login/totp", new { code = "123456" })).StatusCode);
        var third = await second.PostAsync("/auth/login/totp", new { code = "654321" });
        Assert.Equal(LocalSignIn.MfaExpired, (await third.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
        // The step is over, even with a right code: start again with the password.
        var after = await second.PostAsync("/auth/login/totp", new { recoveryCode = codes[1] });
        Assert.Equal(LocalSignIn.MfaExpired, (await after.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
        Assert.Contains(await host.EventsAsync(SecurityEventNames.LoginFailed),
            e => JsonSerializer.Deserialize<Dictionary<string, string>>(e.Data)!["reason"] == "bad_code");

        // Without the password step, there is nothing to answer.
        var stranger = host.Browser();
        var none = await stranger.PostAsync("/auth/login/totp", new { recoveryCode = codes[2] });
        Assert.Equal(LocalSignIn.MfaExpired, (await none.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task New_recovery_codes_replace_the_old_ones_and_turning_the_factor_off_asks_for_the_password_and_a_code()
    {
        await using var host = await HostAsync();
        var alice = await host.SetUpAliceAsync();
        await host.EnrolAsync(alice, "Bob", "bob", BobPassword);
        var bob = await host.SignInAsync("bob", BobPassword);
        var (secret, old) = await EnableTotpAsync(bob);

        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PostAsync("/api/v1/core/me/totp/recovery-codes", new { password = "not my password at all" })).StatusCode);
        var renewed = await bob.PostAsync("/api/v1/core/me/totp/recovery-codes", new { password = BobPassword });
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var fresh = (await renewed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("codes").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Single(await host.EventsAsync(SecurityEventNames.RecoveryCodesRegenerated));

        var phone = host.Browser();
        Assert.Equal("totp", await NextAsync(phone, "bob", BobPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.PostAsync("/auth/login/totp", new { recoveryCode = old[0] })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.PostAsync("/auth/login/totp", new { recoveryCode = fresh[0] })).StatusCode);

        var noCode = await bob.SendJsonAsync(HttpMethod.Delete, "/api/v1/core/me/totp", new { password = BobPassword, code = "" });
        Assert.Equal(HttpStatusCode.BadRequest, noCode.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await bob.SendJsonAsync(HttpMethod.Delete, "/api/v1/core/me/totp", new { password = BobPassword, code = CodeNow(secret, 1) })).StatusCode);
        Assert.Equal("done", await NextAsync(host.Browser(), "bob", BobPassword));
        Assert.Single(await host.EventsAsync(SecurityEventNames.TotpDisabled));
        await using var core = PostgresFixture.NewCoreContext(host.ConnectionString);
        Assert.Empty(await core.RecoveryCodes.ToListAsync(Ct));
    }

    [Fact]
    public async Task Reset_totp_turns_the_factor_off_and_closes_the_adult_s_sessions()
    {
        await using var host = await HostAsync();
        var alice = await host.SetUpAliceAsync();
        var bobId = await host.EnrolAsync(alice, "Bob", "bob", BobPassword);
        var bob = await host.SignInAsync("bob", BobPassword);
        var (secret, codes) = await EnableTotpAsync(bob);
        var pending = host.Browser();
        Assert.Equal("totp", await NextAsync(pending, "bob", BobPassword));

        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await AdminCli.RunAsync(host.Factory.Services, ["reset-totp", "--person", bobId.ToString()],
            new CliConsole(new StringReader(""), output, error, true), Ct);

        Assert.True(exit == 0, error.ToString());
        Assert.Contains("désactivée", output.ToString());
        Assert.False((await bob.SessionAsync()).GetProperty("authenticated").GetBoolean());
        // A code step begun before the reset is over too.
        Assert.Equal(HttpStatusCode.Unauthorized, (await pending.PostAsync("/auth/login/totp", new { code = CodeNow(secret, 1) })).StatusCode);
        Assert.Equal("done", await NextAsync(host.Browser(), "bob", BobPassword));
        Assert.Single(await host.EventsAsync(SecurityEventNames.TotpReset));

        // No secret of the second factor reached the audit.
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select coalesce(string_agg(t::text, '\n'), '') from platform.audit_log t", connection);
        var audit = (string)(await command.ExecuteScalarAsync(Ct))!;
        foreach (var leak in codes.Append(Totp.Base32(secret)).Append(codes[0].Replace("-", "")))
            Assert.DoesNotContain(leak, audit, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(byte[] Secret, List<string> Codes)> EnableTotpAsync(Browser browser)
    {
        var begun = await (await browser.PostAsync("/api/v1/core/me/totp", new { })).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var secret = FromBase32(begun.GetProperty("secret").GetString()!);
        var confirmed = await browser.PostAsync("/api/v1/core/me/totp/confirm", new { code = CodeNow(secret) });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        return (secret, (await confirmed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("codes").EnumerateArray().Select(c => c.GetString()!).ToList());
    }

    private static async Task<string> NextAsync(Browser browser, string login, string password)
    {
        var response = await browser.PostAsync("/auth/login/password", new { login, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("next").GetString()!;
    }

    private static string CodeNow(byte[] secret, int ahead = 0) => Totp.Code(secret, Totp.StepAt(SystemClock.Instance.GetCurrentInstant()) + ahead);

    private static byte[] FromBase32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return [.. bytes];
    }
}
