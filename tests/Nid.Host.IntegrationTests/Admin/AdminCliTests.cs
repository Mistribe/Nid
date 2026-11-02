using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Nid.Host.Admin;
using Nid.Platform.Contracts;
using Nid.Host.IntegrationTests.Audit;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Admin;

/// <summary>1F4 design §3 and §4: the CLI called as a function, against the real host's services and a real base.</summary>
public class AdminCliTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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
        var entry = (await AuditApi.EventsAsync(app, SecurityEventNames.AdminCommand)).Last();
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
    public async Task Rebind_subject_finds_the_adult_by_email_in_any_case()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SetPersonAsync(app, app.AdultB, email: "bob@example.test");

        var run = await CliAsync(app, args: ["rebind-subject", "--person", "BOB@Example.TEST", "--sub", "fresh-sub-b", "--yes"]);

        Assert.Equal(0, run.Exit);
        Assert.Equal("fresh-sub-b", (await PersonAsync(app, app.AdultB)).Subject);
        var logged = await LastCommandAsync(app);
        Assert.Equal(("rebind-subject", "ok", app.AdultB.Value.ToString()), (logged["command"], logged["outcome"], logged["person"]));
        Assert.Equal("admin-cli", Assert.Single(await AuditApi.EventsAsync(app, SecurityEventNames.SubjectRebound)).Via);
    }

    // Review Focus 1: no terminal and no --yes, or another answer than "oui", changes nothing and is still recorded.
    [Theory]
    [InlineData("", false, "admin_cli.confirmation_required")]
    [InlineData("non\n", true, "admin_cli.declined")]
    public async Task Rebind_subject_without_a_confirmation_changes_nothing(string input, bool terminal, string code)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var before = await PersonAsync(app, app.AdultB);

        var run = await CliAsync(app, input, terminal, "rebind-subject", "--person", app.AdultB.Value.ToString(), "--sub", "fresh-sub-b");

        Assert.Equal(1, run.Exit);
        Assert.Equal(before, await PersonAsync(app, app.AdultB));
        var logged = await LastCommandAsync(app);
        Assert.Equal(("refused", code), (logged["outcome"], logged["code"]));
    }

    [Fact]
    public async Task Rebind_subject_asks_and_goes_on_with_oui()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var run = await CliAsync(app, "oui\n", true, "rebind-subject", "--person", app.AdultB.Value.ToString(), "--sub", "fresh-sub-b");

        Assert.Equal(0, run.Exit);
        Assert.Contains("Tapez oui pour confirmer", run.Out);
        Assert.Equal("fresh-sub-b", (await PersonAsync(app, app.AdultB)).Subject);
    }

    [Fact]
    public async Task Rebind_subject_refuses_a_dependent()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var run = await CliAsync(app, args: ["rebind-subject", "--person", app.Dependent.Value.ToString(), "--sub", "fresh-sub", "--yes"]);
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
    public async Task Revoke_tokens_revokes_every_active_token_and_says_how_many()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await TokenApi.CreateAsync(app, app.AdultB);
        await TokenApi.CreateAsync(app, app.AdultB);

        var run = await CliAsync(app, terminal: false, args: ["revoke-tokens", "--person", app.AdultB.Value.ToString()]);

        Assert.Equal(0, run.Exit);
        Assert.Contains("2 jetons révoqués", run.Out);
        Assert.All(await app.GetAsync<List<TokenApi.Token>>(app.AdultB, TokenApi.Tokens), t => Assert.Equal("revoked", t.Status));
        Assert.Equal("revoke-tokens", (await LastCommandAsync(app))["command"]);
        var revoked = Assert.Single(await AuditApi.EventsAsync(app, SecurityEventNames.TokenRevoked));
        Assert.Equal(app.AdultB.Value, revoked.SubjectPersonId);
    }

    [Fact]
    public async Task Revoke_tokens_of_a_person_without_token_records_no_token_revoked()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var run = await CliAsync(app, terminal: false, args: ["revoke-tokens", "--person", app.AdultB.Value.ToString()]);

        Assert.Equal(0, run.Exit);
        Assert.Empty(await AuditApi.EventsAsync(app, SecurityEventNames.TokenRevoked));
    }

    [Fact]
    public async Task Revoking_a_dependents_tokens_is_refused_in_french()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var run = await CliAsync(app, terminal: false, args: ["revoke-tokens", "--person", app.Dependent.Value.ToString()]);

        Assert.Equal(1, run.Exit);
        Assert.Contains("Seul un adulte a des jetons", run.Error);
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
    [InlineData("rebind-subject", "--person", "x")]
    [InlineData("revoke-sessions", "--person")]
    public async Task Usage_errors_exit_two_with_the_help(params string[] args)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var run = await CliAsync(app, args: args);
        Assert.Equal(2, run.Exit);
        Assert.Contains("nid admin", run.Error);
    }

    private static async Task<int> CommandsLogged(NidApp app) => (await AuditApi.EventsAsync(app, SecurityEventNames.AdminCommand)).Count;

    // Final review: one admin_command per run, and every row names the request that did the work.
    [Fact]
    public async Task A_run_records_one_admin_command_and_names_the_command_that_wrote()
    {
        await using var app = await NidApp.StartAsync(fixture);

        await CliAsync(app, args: ["rebind-subject", "--person", app.AdultB.Value.ToString(), "--sub", "fresh-sub-b", "--yes"]);

        Assert.Equal(1, await CommandsLogged(app));
        Assert.Equal("RebindSubjectCommand", Assert.Single(await AuditApi.EventsAsync(app, SecurityEventNames.SubjectRebound)).Request);
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var write = await platform.AuditLog.Where(r => r.Category == "write" && r.Via == "admin-cli").ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(write);
        Assert.All(write, r => Assert.Equal("RebindSubjectCommand", r.Request));
    }

    [Fact]
    public async Task A_usage_error_records_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await CliAsync(app, args: ["rebind-subject"]);
        Assert.Equal(0, await CommandsLogged(app));
    }

    [Fact]
    public async Task A_subject_another_person_holds_leaves_the_adult_unchanged_and_is_recorded()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (_, subjectB, _) = await PersonAsync(app, app.AdultB);
        var before = await PersonAsync(app, app.AdultA);

        await CliAsync(app, args: ["rebind-subject", "--person", app.AdultA.Value.ToString(), "--sub", subjectB!, "--yes"]);

        Assert.Equal(before, await PersonAsync(app, app.AdultA));
        var logged = await LastCommandAsync(app);
        Assert.Equal(("refused", "core.subject_taken"), (logged["outcome"], logged["code"]));
    }

    // Final review (re-graded): a pasted sub keeps no surrounding space, or it would never match a sign-in.
    [Fact]
    public async Task A_pasted_subject_is_trimmed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await CliAsync(app, args: ["rebind-subject", "--person", app.AdultA.Value.ToString(), "--sub", "  pasted-sub \n", "--yes"]);
        Assert.Equal("pasted-sub", (await PersonAsync(app, app.AdultA)).Subject);
    }

    // Final review (re-graded): every refusal reads in French.
    [Fact]
    public async Task Revoking_a_dependents_sessions_is_refused_in_french()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var run = await CliAsync(app, args: ["revoke-sessions", "--person", app.Dependent.Value.ToString()]);
        Assert.Equal(1, run.Exit);
        Assert.Contains("Seul un adulte a des sessions", run.Error);
    }

    // Final review: an unexpected failure (the base gone, a broken table) exits 1 in French, without a stack trace.
    [Fact]
    public async Task An_unexpected_failure_exits_one_in_french_without_a_stack_trace()
    {
        await using var app = await NidApp.StartAsync(fixture);
        _ = app.Services;
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            await core.Database.ExecuteSqlRawAsync("alter table core.person rename to person_gone", cancellationToken: TestContext.Current.CancellationToken);

        var run = await CliAsync(app, args: ["list-persons"]);

        Assert.Equal(1, run.Exit);
        Assert.Contains("Erreur inattendue", run.Error);
        Assert.DoesNotContain("   at ", run.Error);
    }

    // Final review: a run whose trace could not be written says so.
    [Fact]
    public async Task A_run_whose_admin_command_could_not_be_written_says_so()
    {
        await using var app = await NidApp.StartAsync(fixture, services => services.AddScoped<ISecurityEvents, LosingSecurityEvents>());

        var run = await CliAsync(app, args: ["revoke-sessions", "--person", app.AdultB.Value.ToString()]);

        Assert.Equal(0, run.Exit);
        Assert.Contains("n'a pas pu être enregistrée au journal", run.Error);
    }

    private sealed class LosingSecurityEvents : ISecurityEvents
    {
        public Task<bool> RecordAsync(SecurityEvent securityEvent, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RecordAsync(SecurityEvent securityEvent, Actor actor, CancellationToken ct) => Task.FromResult(false);
    }

    [Fact]
    public async Task Help_lists_the_commands()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var run = await CliAsync(app, args: ["--help"]);
        Assert.Equal(0, run.Exit);
        foreach (var command in new[] { "list-persons", "revoke-sessions", "revoke-tokens", "rebind-subject" })
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
                "delete from platform.__ef_migrations_history where \"MigrationId\" = (select max(\"MigrationId\") from platform.__ef_migrations_history)", cancellationToken: TestContext.Current.CancellationToken);

        var before = await PersonAsync(app, app.AdultB);

        var run = await CliAsync(app, args: ["rebind-subject", "--person", app.AdultB.Value.ToString(), "--sub", "fresh-sub-b", "--yes"]);

        Assert.Equal(1, run.Exit);
        Assert.Contains("démarrez d'abord nid,", run.Error);
        Assert.Equal(before, await PersonAsync(app, app.AdultB));
    }
}
