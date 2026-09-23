using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Nid.Platform.Contracts;
using Nid.Platform.Import;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Import;

[Collection("postgres")]
public class ValidateCommitTests(PostgresFixture fixture)
{
    private const string OneDependent = """[{"ref":"kid","kind":"dependent","display_name":"Léa"}]""";

    [Fact]
    public async Task Only_a_validated_import_can_be_committed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);
        await client.PutAsync(app.AdultA, session.Id, "persons", 0, OneDependent);

        var response = await client.CommitRawAsync(app.AdultA, session.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ImportCodes.State, await ImportClient.CodeOf(response));
    }

    [Fact]
    public async Task A_batch_after_validation_opens_the_session_again()
    {
        await using var app = await NidApp.StartAsync(fixture, services => services.AddScoped<IImportSection, StubSection>());
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);
        await client.PutAsync(app.AdultA, session.Id, "persons", 0, OneDependent);
        Assert.Equal("validated", (await client.ValidateAsync(app.AdultA, session.Id)).State);

        await client.PutAsync(app.AdultA, session.Id, StubSection.Name, 0, """[{"id":"a"}]""");

        Assert.Equal("open", (await client.GetAsync(app.AdultA, session.Id)).State);
    }

    [Fact]
    public async Task Batches_are_numbered_from_zero_without_gaps()
    {
        await using var app = await NidApp.StartAsync(fixture, services =>
            services.AddSingleton(new ImportOptions { MaxEntitiesPerBatch = 1 }));
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);
        await client.PutAsync(app.AdultA, session.Id, "persons", 0, OneDependent);
        await client.PutAsync(app.AdultA, session.Id, "persons", 2, """[{"ref":"kid-2","kind":"dependent","display_name":"Tom"}]""");

        var report = (await client.ValidateAsync(app.AdultA, session.Id)).Report!;

        Assert.Equal("invalid", report.State);
        Assert.Equal(ImportCodes.BatchLayout, Assert.Single(report.Issues).Code);
    }

    [Fact]
    public async Task A_validation_out_of_time_is_refused_as_too_large()
    {
        await using var app = await NidApp.StartAsync(fixture, services =>
            services.AddSingleton(new ImportOptions { OperationTimeout = TimeSpan.Zero }));
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);
        await client.PutAsync(app.AdultA, session.Id, "persons", 0, OneDependent);

        var response = await client.ValidateRawAsync(app.AdultA, session.Id);

        Assert.Equal(ImportCodes.TooLarge, await ImportClient.CodeOf(response));
        Assert.Equal("open", (await client.GetAsync(app.AdultA, session.Id)).State);
    }

    [Fact]
    public async Task A_committed_session_keeps_its_report_and_no_staging()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);
        await client.PutAsync(app.AdultA, session.Id, "persons", 0, OneDependent);
        await client.ValidateAsync(app.AdultA, session.Id);

        Assert.Equal(HttpStatusCode.OK, (await client.CommitRawAsync(app.AdultA, session.Id)).StatusCode);

        var after = await client.GetAsync(app.AdultA, session.Id);
        Assert.Equal("committed", after.State);
        Assert.Equal(new CountsResponse(1, 0, 0), after.Report!.Sections["persons"]);
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        Assert.False(platform.Batches.Any(b => b.SessionId == session.Id));
    }

    [Fact]
    public async Task Another_person_can_neither_validate_nor_commit_a_session()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);

        Assert.Equal(HttpStatusCode.NotFound, (await client.ValidateRawAsync(app.AdultB, session.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.CommitRawAsync(app.AdultB, session.Id)).StatusCode);
    }
}
