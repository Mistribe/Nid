using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Host.Admin;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Host.IntegrationTests.Admin;

/// <summary>Issue 215, local-accounts.md §15: `create-admin`, `reset-password` and `unlock`, with local accounts on.</summary>
public class LocalAccountCliTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Dictionary<string, string> Local = new() { ["Auth:Mode"] = "local" };

    private sealed record Run(int Exit, string Out, string Error);

    private static async Task<Run> CliAsync(NidApp app, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await AdminCli.RunAsync(app.Services, args, new CliConsole(new StringReader(""), output, error, true));
        return new Run(exit, output.ToString(), error.ToString());
    }

    private static string LinkIn(string output)
        => output.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("https://", StringComparison.Ordinal));

    [Fact]
    public async Task Create_admin_makes_a_pending_admin_and_prints_an_enrolment_link_that_the_audit_never_sees()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: Local);
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            // Room for a third adult: the test household has two.
            if (await core.HouseholdSettings.SingleOrDefaultAsync(TestContext.Current.CancellationToken) is { } settings) settings.MaxAdults = 3;
            else core.HouseholdSettings.Add(new HouseholdSettingsRow { MaxAdults = 3 });
            await core.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var run = await CliAsync(app, "create-admin", "--name", "Carol");
        Assert.True(run.Exit == 0, run.Error);

        Assert.Equal(0, run.Exit);
        var link = LinkIn(run.Out);
        Assert.StartsWith("https://test.localhost/password#nid_link_", link);
        await using var db = PostgresFixture.NewCoreContext(app.ConnectionString);
        var carol = await db.Persons.SingleAsync(p => p.DisplayName == "Carol", TestContext.Current.CancellationToken);
        Assert.True(carol.IsAdmin);
        Assert.Equal("enroll", (await db.CredentialLinks.SingleAsync(l => l.PersonId == carol.Id, TestContext.Current.CancellationToken)).Purpose);
        var command = (await AuditApi.EventsAsync(app, SecurityEventNames.AdminCommand)).Last();
        Assert.DoesNotContain("nid_link_", command.Data);
        Assert.Equal("ok", JsonSerializer.Deserialize<Dictionary<string, string>>(command.Data)!["outcome"]);
    }

    [Fact]
    public async Task Create_admin_refuses_past_the_household_cap()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: Local);

        var run = await CliAsync(app, "create-admin", "--name", "Carol");

        Assert.Equal(1, run.Exit);
        Assert.Contains("nombre maximal d'adultes", run.Error);
    }

    [Fact]
    public async Task Reset_password_prints_a_reset_link_and_unlocks_the_account_and_unlock_clears_a_lock()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: Local);
        await using (var db = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            db.LocalCredentials.Add(new LocalCredentialRow
            {
                PersonId = app.AdultB.Value, Login = "bob", PasswordHash = "x", LockLevel = 2, FailedCount = 1,
                LockedUntil = SystemClock.Instance.GetCurrentInstant().Plus(NodaTime.Duration.FromMinutes(30)),
                PasswordChangedAt = SystemClock.Instance.GetCurrentInstant(), CreatedAt = SystemClock.Instance.GetCurrentInstant(),
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var unlock = await CliAsync(app, "unlock", "--person", app.AdultB.Value.ToString());
        Assert.Equal(0, unlock.Exit);
        await using (var db = PostgresFixture.NewCoreContext(app.ConnectionString))
            Assert.Null((await db.LocalCredentials.SingleAsync(c => c.PersonId == app.AdultB.Value, TestContext.Current.CancellationToken)).LockedUntil);

        var reset = await CliAsync(app, "reset-password", "--person", app.AdultB.Value.ToString(), "--revoke-tokens");
        Assert.Equal(0, reset.Exit);
        Assert.Contains("nouveau mot de passe", reset.Out);
        Assert.Contains("jetons d'accès sont révoqués", reset.Out);
        await using (var db = PostgresFixture.NewCoreContext(app.ConnectionString))
            Assert.Equal("reset", (await db.CredentialLinks.SingleAsync(l => l.PersonId == app.AdultB.Value, TestContext.Current.CancellationToken)).Purpose);
        Assert.Single(await AuditApi.EventsAsync(app, SecurityEventNames.CredentialLinkIssued));
    }

    [Fact]
    public async Task Unlock_of_an_adult_without_a_local_account_says_so()
    {
        await using var app = await NidApp.StartAsync(fixture, settings: Local);

        var run = await CliAsync(app, "unlock", "--person", app.AdultA.Value.ToString());

        Assert.Equal(1, run.Exit);
        Assert.Contains("pas de compte local", run.Error);
    }

    [Fact]
    public async Task Without_local_accounts_the_commands_refuse()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var run = await CliAsync(app, "reset-password", "--person", app.AdultA.Value.ToString());

        Assert.Equal(1, run.Exit);
        Assert.Contains("Auth__Mode", run.Error);
    }
}
