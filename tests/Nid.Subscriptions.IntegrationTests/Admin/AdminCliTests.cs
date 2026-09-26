using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Host.Admin;
using Nid.Platform.Contracts;
using Nid.Subscriptions.IntegrationTests.Audit;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Admin;

/// <summary>1F4 design §3 and §4: the CLI called as a function, against the real host's services and a real base.</summary>
[Collection("postgres")]
public class AdminCliTests(PostgresFixture fixture)
{
    private sealed record Run(int Exit, string Out, string Error);

    private static async Task<Run> CliAsync(NidApp app, string input = "", bool terminal = true, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await AdminCli.RunAsync(app.Services, args, new CliConsole(new StringReader(input), output, error, terminal));
        return new Run(exit, output.ToString(), error.ToString());
    }

    private static async Task SetPersonAsync(NidApp app, PersonId id, string? email = null, string? name = null)
    {
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var row = await core.Persons.SingleAsync(p => p.Id == id.Value);
        if (email is not null) row.Email = email;
        if (name is not null) row.DisplayName = name;
        await core.SaveChangesAsync();
    }

    private static async Task<(bool IsAdmin, string? Subject, int Epoch)> PersonAsync(NidApp app, PersonId id)
    {
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var row = await core.Persons.AsNoTracking().SingleAsync(p => p.Id == id.Value);
        return (row.IsAdmin, row.OidcSubject, row.SessionEpoch);
    }

    private static async Task<Dictionary<string, string>> LastCommandAsync(NidApp app)
    {
        var entry = (await SecurityEventTests.EventsAsync(app, SecurityEventNames.AdminCommand)).Last();
        Assert.Equal("admin-cli", entry.Via);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(entry.Data)!;
    }

    [Fact]
    public async Task List_persons_shows_adults_and_dependents_and_never_a_subject_value()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (_, subject, _) = await PersonAsync(app, app.AdultA);

        var run = await CliAsync(app, args: ["list-persons"]);

        Assert.Equal(0, run.Exit);
        Assert.Contains(app.AdultA.Value.ToString(), run.Out);
        Assert.Contains(app.Dependent.Value.ToString(), run.Out);
        Assert.Contains("enfant", run.Out);
        Assert.DoesNotContain(subject!, run.Out);
        Assert.Equal("ok", (await LastCommandAsync(app))["outcome"]);
    }

    [Fact]
    public async Task Grant_admin_with_yes_makes_the_adult_admin_found_by_email_in_any_case()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SetPersonAsync(app, app.AdultB, email: "bob@example.test");

        var run = await CliAsync(app, args: ["grant-admin", "--person", "BOB@Example.TEST", "--yes"]);

        Assert.Equal(0, run.Exit);
        Assert.True((await PersonAsync(app, app.AdultB)).IsAdmin);
        var logged = await LastCommandAsync(app);
        Assert.Equal(("grant-admin", "ok", app.AdultB.Value.ToString()), (logged["command"], logged["outcome"], logged["person"]));
        Assert.Equal("admin-cli", Assert.Single(await SecurityEventTests.EventsAsync(app, SecurityEventNames.AdminGranted)).Via);
    }

    // Review Focus 1: no terminal and no --yes, or another answer than "oui", changes nothing and is still recorded.
    [Theory]
    [InlineData("", false, "admin_cli.confirmation_required")]
    [InlineData("non\n", true, "admin_cli.declined")]
    public async Task Grant_admin_without_a_confirmation_changes_nothing(string input, bool terminal, string code)
    {
        await using var app = await NidApp.StartAsync(fixture);

        var run = await CliAsync(app, input, terminal, "grant-admin", "--person", app.AdultB.Value.ToString());

        Assert.Equal(1, run.Exit);
        Assert.False((await PersonAsync(app, app.AdultB)).IsAdmin);
        var logged = await LastCommandAsync(app);
        Assert.Equal(("refused", code), (logged["outcome"], logged["code"]));
    }

    [Fact]
    public async Task Grant_admin_asks_and_goes_on_with_oui()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var run = await CliAsync(app, "oui\n", true, "grant-admin", "--person", app.AdultB.Value.ToString());

        Assert.Equal(0, run.Exit);
        Assert.Contains("Tapez oui pour confirmer", run.Out);
        Assert.True((await PersonAsync(app, app.AdultB)).IsAdmin);
    }

    [Fact]
    public async Task Grant_admin_refuses_a_dependent()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var run = await CliAsync(app, args: ["grant-admin", "--person", app.Dependent.Value.ToString(), "--yes"]);
        Assert.Equal(1, run.Exit);
        Assert.Equal("refused", (await LastCommandAsync(app))["outcome"]);
    }

    [Fact]
    public async Task Revoke_sessions_raises_the_epoch_without_asking()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (_, _, epoch) = await PersonAsync(app, app.AdultB);

        var run = await CliAsync(app, terminal: false, args: ["revoke-sessions", "--person", app.AdultB.Value.ToString()]);

        Assert.Equal(0, run.Exit);
        Assert.Equal(epoch + 1, (await PersonAsync(app, app.AdultB)).Epoch);
    }

    [Fact]
    public async Task Rebind_subject_records_both_subjects_in_admin_command()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (_, previous, epoch) = await PersonAsync(app, app.AdultA);

        var run = await CliAsync(app, args: ["rebind-subject", "--person", app.AdultA.Value.ToString(), "--sub", "fresh-sub", "--yes"]);

        Assert.Equal(0, run.Exit);
        Assert.Equal(("fresh-sub", epoch + 1), ((await PersonAsync(app, app.AdultA)).Subject, (await PersonAsync(app, app.AdultA)).Epoch));
        var logged = await LastCommandAsync(app);
        Assert.Equal(("fresh-sub", previous), (logged["sub"], logged["previous_sub"]));
    }

    [Fact]
    public async Task Rebind_subject_onto_the_same_subject_says_so_and_exits_zero()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (_, subject, _) = await PersonAsync(app, app.AdultA);

        var run = await CliAsync(app, args: ["rebind-subject", "--person", app.AdultA.Value.ToString(), "--sub", subject!, "--yes"]);

        Assert.Equal(0, run.Exit);
        Assert.Equal("unchanged", (await LastCommandAsync(app))["outcome"]);
    }

    [Fact]
    public async Task Rebind_subject_onto_another_persons_subject_names_that_person()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (_, subjectB, _) = await PersonAsync(app, app.AdultB);
        await SetPersonAsync(app, app.AdultB, name: "Bob Dev");

        var run = await CliAsync(app, args: ["rebind-subject", "--person", app.AdultA.Value.ToString(), "--sub", subjectB!, "--yes"]);

        Assert.Equal(1, run.Exit);
        Assert.Contains("Bob Dev", run.Error);
        Assert.Contains(app.AdultB.Value.ToString(), run.Error);
    }

    // Review Focus 3: a name shared by two persons refuses and lists them; an unknown one points to list-persons.
    [Fact]
    public async Task An_ambiguous_name_refuses_and_lists_the_matches()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SetPersonAsync(app, app.AdultA, name: "Sam");
        await SetPersonAsync(app, app.AdultB, name: "Sam");

        var ambiguous = await CliAsync(app, args: ["revoke-sessions", "--person", "Sam"]);
        var unknown = await CliAsync(app, args: ["revoke-sessions", "--person", "Personne"]);

        Assert.Equal(1, ambiguous.Exit);
        Assert.Contains(app.AdultA.Value.ToString(), ambiguous.Error);
        Assert.Contains(app.AdultB.Value.ToString(), ambiguous.Error);
        Assert.Equal(1, unknown.Exit);
        Assert.Contains("nid admin list-persons", unknown.Error);
    }

    [Theory]
    [InlineData("fly")]
    [InlineData("grant-admin")]
    [InlineData("rebind-subject", "--person", "x")]
    [InlineData("revoke-sessions", "--person")]
    public async Task Usage_errors_exit_two_with_the_help(params string[] args)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var run = await CliAsync(app, args: args);
        Assert.Equal(2, run.Exit);
        Assert.Contains("nid admin", run.Error);
    }

    [Fact]
    public async Task Help_lists_the_commands()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var run = await CliAsync(app, args: ["--help"]);
        Assert.Equal(0, run.Exit);
        foreach (var command in new[] { "list-persons", "grant-admin", "revoke-sessions", "rebind-subject" })
            Assert.Contains(command, run.Out);
    }

    // Review Focus 4: a base behind the code is refused before any write.
    [Fact]
    public async Task A_pending_migration_makes_the_cli_refuse()
    {
        await using var app = await NidApp.StartAsync(fixture);
        // The factory starts the host, which migrates, on first use: start it before the history loses a row.
        _ = app.Services;
        await using (var platform = PostgresFixture.NewPlatformContext(app.ConnectionString))
            await platform.Database.ExecuteSqlRawAsync(
                "delete from platform.__ef_migrations_history where \"MigrationId\" = (select max(\"MigrationId\") from platform.__ef_migrations_history)");

        var run = await CliAsync(app, args: ["grant-admin", "--person", app.AdultB.Value.ToString(), "--yes"]);

        Assert.Equal(1, run.Exit);
        Assert.Contains("démarrez d'abord nid-api", run.Error);
        Assert.False((await PersonAsync(app, app.AdultB)).IsAdmin);
    }
}
