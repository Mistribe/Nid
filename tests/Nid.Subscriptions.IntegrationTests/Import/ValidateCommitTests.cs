using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Platform.Contracts;
using Nid.Platform.Import;
using Nid.TestSupport;
using UnambitiousFx.Functional;

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

    [Fact]
    public async Task A_section_that_throws_while_applying_rolls_back_and_leaves_the_session_validated()
    {
        await using var app = await NidApp.StartAsync(fixture, services => services.AddScoped<IImportSection, ThrowingApplySection>());
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);
        await client.PutAsync(app.AdultA, session.Id, "persons", 0, OneDependent);
        Assert.Equal("validated", (await client.ValidateAsync(app.AdultA, session.Id)).State);

        var response = await client.CommitRawAsync(app.AdultA, session.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ImportCodes.NotCommitted, await ImportClient.CodeOf(response));
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            Assert.False(await core.Persons.AnyAsync(p => p.DisplayName == "Léa"));
        var after = await client.GetAsync(app.AdultA, session.Id);
        Assert.Equal("validated", after.State);
        Assert.Contains(after.Report!.Issues, i => i.Code == ImportCodes.ApplyFailed);
    }

    [Fact]
    public async Task A_commit_out_of_time_rolls_back_and_leaves_the_session_validated()
    {
        await using var app = await NidApp.StartAsync(fixture, services =>
        {
            services.AddScoped<IImportSection, SlowApplySection>();
            services.AddSingleton(new ImportOptions { OperationTimeout = TimeSpan.FromSeconds(3) });
        });
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);
        await client.PutAsync(app.AdultA, session.Id, "persons", 0, OneDependent);
        Assert.Equal("validated", (await client.ValidateAsync(app.AdultA, session.Id)).State);

        var response = await client.CommitRawAsync(app.AdultA, session.Id);

        Assert.Equal(ImportCodes.TooLarge, await ImportClient.CodeOf(response));
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            Assert.False(await core.Persons.AnyAsync(p => p.DisplayName == "Léa"));
        var after = await client.GetAsync(app.AdultA, session.Id);
        Assert.Equal("validated", after.State);
        Assert.Contains(after.Report!.Issues, i => i.Code == ImportCodes.TooLarge);
    }

    /// <summary>A section, past <c>persons</c> in order, whose apply step throws instead of failing a <c>Result</c>.</summary>
    private sealed class ThrowingApplySection : IImportSection
    {
        public Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>>(new Dictionary<string, IReadOnlyList<JsonElement>>());

        public string Module => "test";
        public IReadOnlyList<string> Sections => ["throwing_things"];
        public int Order => 100;

        public IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex) => [];

        public Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
            => Task.FromResult(new SectionOutcome([], new Dictionary<string, SectionCounts>()));

        public Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
            => throw new InvalidOperationException("Applying 'throwing_things' always fails, on purpose.");
    }

    /// <summary>A section whose apply step never returns on its own — only a commit timeout ends it.</summary>
    private sealed class SlowApplySection : IImportSection
    {
        public Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>>(new Dictionary<string, IReadOnlyList<JsonElement>>());

        public string Module => "test";
        public IReadOnlyList<string> Sections => ["slow_things"];
        public int Order => 100;

        public IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex) => [];

        public Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
            => Task.FromResult(new SectionOutcome([], new Dictionary<string, SectionCounts>()));

        public async Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Result.Success<IReadOnlyDictionary<string, SectionCounts>>(new Dictionary<string, SectionCounts>());
        }
    }
}
