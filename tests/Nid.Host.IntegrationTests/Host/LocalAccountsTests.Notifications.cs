using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Host.Admin;
using Nid.Platform.Infrastructure.Persistence;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>#291, local-accounts.md §16: the holder of a local account is told what happened to it, and nobody else is.</summary>
public partial class LocalAccountsTests
{
    [Fact]
    public async Task The_holder_is_told_of_a_lock_a_reset_link_and_its_use_and_nobody_else_is()
    {
        await using var host = await HostAsync();
        var alice = await host.SetUpAliceAsync();
        var bob = await host.EnrolAsync(alice, "Bob", "bob", BobPassword);
        Assert.Empty(await NoticesAsync(host)); // an enrolment link tells nobody

        for (var i = 0; i < 5; i++)
            await host.Browser().PostAsync("/auth/login/password", new { login = "bob", password = $"wrong guess number {i}" });
        var token = Token(await alice.PasswordLinkAsync(bob));
        Assert.Equal(HttpStatusCode.OK, (await host.Browser().PostAsync("/auth/link/complete", new { token, password = "a brand new sentence for bob" })).StatusCode);

        var notices = await NoticesAsync(host);
        Assert.All(notices, n => Assert.Equal(bob, n.PersonId));
        Assert.Equal(["login_locked", "password_link_issued", "password_reset"], notices.Select(n => n.Kind).Order().ToArray());
        Assert.All(notices, n => Assert.Equal("critical", n.Level));
        // Never the link, its id or the identifier.
        Assert.All(notices, n => Assert.Equal("{}", n.Params.Replace(" ", "")));
        Assert.DoesNotContain(notices, n => n.Key.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changing_my_password_tells_me()
    {
        await using var host = await HostAsync();
        var alice = await host.SetUpAliceAsync();
        var bobId = await host.EnrolAsync(alice, "Bob", "bob", BobPassword);
        var bob = await host.SignInAsync("bob", BobPassword);
        await bob.SessionAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await bob.PutAsync("/api/v1/core/me/password", new { current = BobPassword, @new = "a fresh sentence for bob" })).StatusCode);

        var notice = Assert.Single(await NoticesAsync(host));
        Assert.Equal(("password_changed", bobId), (notice.Kind, notice.PersonId));
        Assert.Equal("password", JsonSerializer.Deserialize<Dictionary<string, string>>(notice.Params)!["field"]);
    }

    [Fact]
    public async Task Two_recovery_codes_left_warn_until_new_ones_are_made_and_reset_totp_tells_the_holder()
    {
        await using var host = await HostAsync();
        var alice = await host.SetUpAliceAsync();
        var bobId = await host.EnrolAsync(alice, "Bob", "bob", BobPassword);
        var bob = await host.SignInAsync("bob", BobPassword);
        var (_, codes) = await EnableTotpAsync(bob);

        foreach (var code in codes.Take(8))
        {
            var phone = host.Browser();
            Assert.Equal("totp", await NextAsync(phone, "bob", BobPassword));
            Assert.Equal(HttpStatusCode.OK, (await phone.PostAsync("/auth/login/totp", new { recoveryCode = code })).StatusCode);
        }
        var low = Assert.Single(await NoticesAsync(host));
        Assert.Equal(("recovery_codes_low", bobId, "action"), (low.Kind, low.PersonId, low.Level));
        Assert.NotEqual("resolved", low.State);

        await bob.SessionAsync();
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync("/api/v1/core/me/totp/recovery-codes", new { password = BobPassword })).StatusCode);
        Assert.Equal("resolved", Assert.Single(await NoticesAsync(host)).State);

        var exit = await AdminCli.RunAsync(host.Factory.Services, ["reset-totp", "--person", bobId.ToString()],
            new CliConsole(new StringReader(""), new StringWriter(), new StringWriter(), true), Ct);
        Assert.Equal(0, exit);
        var reset = Assert.Single(await NoticesAsync(host), n => n.Kind == "totp_reset");
        Assert.Equal(bobId, reset.PersonId);
    }

    private static async Task<List<NotificationRow>> NoticesAsync(TestHost host)
    {
        await using var platform = PostgresFixture.NewPlatformContext(host.ConnectionString);
        return await platform.Notifications.AsNoTracking().OrderBy(n => n.CreatedAt).ToListAsync(Ct);
    }
}
