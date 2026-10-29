using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Import;

public class PersonsImportTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Adults_are_resolved_by_the_map_given_at_the_start()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA, new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value });
        await client.PutAsync(app.AdultA, session.Id, "persons", 0, """
            [{"ref":"adult-1","kind":"adult","display_name":"Adulte 1"},{"ref":"adult-2","kind":"adult","display_name":"Adulte 2"}]
            """);

        var validated = await client.ValidateAsync(app.AdultA, session.Id);

        Assert.Equal("validated", validated.State);
        Assert.Equal(new CountsResponse(0, 2, 0), validated.Report!.Sections["persons"]);
    }

    [Fact]
    public async Task An_adult_left_out_of_the_map_is_found_by_email()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            await core.Persons.Where(p => p.Id == app.AdultA.Value).ExecuteUpdateAsync(u => u.SetProperty(p => p.Email, "a@example.org"));
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);
        await client.PutAsync(app.AdultA, session.Id, "persons", 0, """
            [{"ref":"adult-1","kind":"adult","display_name":"A","email":"A@Example.org"}]
            """);

        Assert.Equal("validated", (await client.ValidateAsync(app.AdultA, session.Id)).State);
    }

    [Fact]
    public async Task An_unresolved_adult_is_an_error_that_names_the_candidates()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);
        await client.PutAsync(app.AdultA, session.Id, "persons", 0, """
            [{"ref":"adult-1","kind":"adult","display_name":"Nobody"}]
            """);

        var report = (await client.ValidateAsync(app.AdultA, session.Id)).Report!;

        var issue = Assert.Single(report.Issues);
        Assert.Equal(ImportCodes.AdultUnresolved, issue.Code);
        Assert.Equal("persons[0]", issue.Path);
        Assert.Contains(app.AdultA.Value.ToString(), issue.Message);
        Assert.Contains(app.AdultB.Value.ToString(), issue.Message);
        Assert.Equal("invalid", report.State);
    }

    [Fact]
    public async Task The_map_cannot_make_a_dependent_an_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA, new Dictionary<string, Guid> { ["adult-1"] = app.Dependent.Value });
        await client.PutAsync(app.AdultA, session.Id, "persons", 0, """[{"ref":"adult-1","kind":"adult","display_name":"X"}]""");

        var report = (await client.ValidateAsync(app.AdultA, session.Id)).Report!;

        Assert.Equal(ImportCodes.AdultMapInvalid, Assert.Single(report.Issues).Code);
    }

    [Fact]
    public async Task A_known_dependent_is_reused_and_a_new_one_is_created_only_at_commit()
    {
        await using var app = await NidApp.StartAsync(fixture);
        string known;
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            known = (await core.Persons.SingleAsync(p => p.Id == app.Dependent.Value)).DisplayName;
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);
        await client.PutAsync(app.AdultA, session.Id, "persons", 0, $$"""
            [{"ref":"kid-1","kind":"dependent","display_name":"{{known}}"},{"ref":"kid-2","kind":"dependent","display_name":"Léa"}]
            """);

        var validated = await client.ValidateAsync(app.AdultA, session.Id);
        Assert.Equal(new CountsResponse(1, 1, 0), validated.Report!.Sections["persons"]);
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            Assert.False(await core.Persons.AnyAsync(p => p.DisplayName == "Léa")); // validate writes nothing

        var committed = await client.CommitRawAsync(app.AdultA, session.Id);

        Assert.True(committed.IsSuccessStatusCode, await committed.Content.ReadAsStringAsync());
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
            Assert.Equal("dependent", (await core.Persons.SingleAsync(p => p.DisplayName == "Léa")).Kind);
        Assert.Equal("committed", (await client.GetAsync(app.AdultA, session.Id)).State);
    }

    [Fact]
    public async Task Refs_are_unique_across_batches()
    {
        await using var app = await NidApp.StartAsync(fixture, services =>
            services.AddSingleton(new Nid.Platform.Import.ImportOptions { MaxEntitiesPerBatch = 1 }));
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);
        await client.PutAsync(app.AdultA, session.Id, "persons", 0, """[{"ref":"kid","kind":"dependent","display_name":"One"}]""");
        await client.PutAsync(app.AdultA, session.Id, "persons", 1, """[{"ref":"kid","kind":"dependent","display_name":"Two"}]""");

        var issue = Assert.Single((await client.ValidateAsync(app.AdultA, session.Id)).Report!.Issues);

        Assert.Equal(ImportCodes.DuplicateId, issue.Code);
        Assert.Equal("persons[1].ref", issue.Path);
    }

    [Theory]
    [InlineData("""[{"ref":"Kid_1","kind":"dependent","display_name":"X"}]""", ImportCodes.InvalidId, "persons[0].ref")]
    [InlineData("""[{"ref":"kid","kind":"child","display_name":"X"}]""", ImportCodes.InvalidValue, "persons[0].kind")]
    [InlineData("""[{"ref":"kid","kind":"dependent","display_name":" "}]""", ImportCodes.InvalidValue, "persons[0].display_name")]
    [InlineData("""[{"ref":"kid","kind":"dependent","display_name":"X","age":7}]""", ImportCodes.MalformedEntity, "persons[0]")]
    public async Task A_malformed_person_is_refused_on_receipt(string entities, string code, string pathPrefix)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);

        var batch = await client.PutAsync(app.AdultA, session.Id, "persons", 0, entities);

        Assert.False(batch.Accepted);
        var issue = Assert.Single(batch.Issues);
        Assert.Equal(code, issue.Code);
        Assert.StartsWith(pathPrefix, issue.Path);
    }
}
