using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application.Admin;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Host.IntegrationTests.Audit;
using Nid.TestSupport;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.IntegrationTests.Admin;

/// <summary>1F4 design §3: rebinding an adult's OIDC subject, and listing every person.</summary>
public class RebindSubjectTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<Result<T>> AsCliAsync<T>(NidApp app, IRequest<T> request) where T : notnull
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Cli;
        return await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(request);
    }

    private static async Task<(string? Subject, int Epoch)> PersonAsync(NidApp app, PersonId id)
    {
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var row = await core.Persons.AsNoTracking().SingleAsync(p => p.Id == id.Value);
        return (row.OidcSubject, row.SessionEpoch);
    }

    [Fact]
    public async Task Rebinding_changes_the_subject_and_ends_the_sessions_together()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (_, epoch) = await PersonAsync(app, app.AdultA);

        var result = await AsCliAsync(app, new RebindSubjectCommand(app.AdultA, "new-sub-a"));

        Assert.True(result.TryGetValue(out var outcome));
        Assert.True(outcome!.Changed);
        Assert.NotNull(outcome.PreviousSubject);
        Assert.Equal(("new-sub-a", epoch + 1), await PersonAsync(app, app.AdultA));
        var rebound = Assert.Single(await AuditApi.EventsAsync(app, SecurityEventNames.SubjectRebound));
        Assert.Equal(("admin-cli", app.AdultA.Value), (rebound.Via, rebound.SubjectPersonId!.Value));
        Assert.Equal("true", System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(rebound.Data)!["had_subject"]);
    }

    // Review Focus 2: a subject another person holds changes nothing on either.
    [Fact]
    public async Task A_subject_another_person_holds_is_refused_and_changes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (subjectB, _) = await PersonAsync(app, app.AdultB);
        var before = await PersonAsync(app, app.AdultA);

        var result = await AsCliAsync(app, new RebindSubjectCommand(app.AdultA, subjectB!));

        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal(PersonCodes.SubjectTaken, failure!.Code);
        Assert.Equal(app.AdultB.Value.ToString(), failure.Metadata!["person"]?.ToString());
        Assert.Equal(before, await PersonAsync(app, app.AdultA));
        Assert.Empty(await AuditApi.EventsAsync(app, SecurityEventNames.SubjectRebound));
    }

    [Fact]
    public async Task The_same_subject_changes_nothing_and_records_no_event()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var before = await PersonAsync(app, app.AdultA);

        var result = await AsCliAsync(app, new RebindSubjectCommand(app.AdultA, before.Subject!));

        Assert.True(result.TryGetValue(out var outcome));
        Assert.False(outcome!.Changed);
        Assert.Equal(before, await PersonAsync(app, app.AdultA));
        Assert.Empty(await AuditApi.EventsAsync(app, SecurityEventNames.SubjectRebound));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_subject_is_refused(string subject)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var result = await AsCliAsync(app, new RebindSubjectCommand(app.AdultA, subject));
        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal(PersonCodes.SubjectInvalid, failure!.Code);
    }

    [Fact]
    public async Task A_dependent_cannot_be_bound()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var result = await AsCliAsync(app, new RebindSubjectCommand(app.Dependent, "kid-sub"));
        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal(PersonCodes.NotAnAdult, failure!.Code);
    }

    [Fact]
    public async Task Every_person_is_listed_with_whether_a_subject_is_bound_never_its_value()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var result = await AsCliAsync(app, new ListAllPersonsQuery());

        Assert.True(result.TryGetValue(out var persons));
        Assert.Contains(persons!, p => p.Id == app.AdultA.Value && p.Kind == "adult" && p.HasSubject);
        Assert.Contains(persons!, p => p.Id == app.Dependent.Value && p.Kind == "dependent" && !p.HasSubject);
    }
}
