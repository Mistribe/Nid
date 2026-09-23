using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Import;

[Collection("postgres")]
public class BatchTests(PostgresFixture fixture)
{
    private Task<NidApp> StartAsync()
        => NidApp.StartAsync(fixture, services => services.AddScoped<IImportSection, StubSection>());

    [Fact]
    public async Task An_accepted_batch_is_staged_once_even_when_sent_again()
    {
        await using var app = await StartAsync();
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);

        var first = await client.PutAsync(app.AdultA, session.Id, StubSection.Name, 0, """[{"id":"a"},{"id":"b"}]""");
        var replay = await client.PutAsync(app.AdultA, session.Id, StubSection.Name, 0, """[{"id":"a"},{"id":"b"}]""");

        Assert.True(first.Accepted);
        Assert.Equal(2, first.Count);
        Assert.True(replay.Accepted);
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        Assert.Equal(1, await platform.Batches.CountAsync(b => b.SessionId == session.Id));
    }

    [Fact]
    public async Task A_taken_slot_refuses_different_content()
    {
        await using var app = await StartAsync();
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);
        await client.PutAsync(app.AdultA, session.Id, StubSection.Name, 0, """[{"id":"a"}]""");

        var other = await client.PutRawAsync(app.AdultA, session.Id, StubSection.Name, 0, """[{"id":"z"}]""");

        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Equal(ImportCodes.SlotTaken, await ImportClient.CodeOf(other));
    }

    [Fact]
    public async Task A_hash_that_does_not_match_the_content_is_refused()
    {
        await using var app = await StartAsync();
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);

        var response = await client.PutRawAsync(app.AdultA, session.Id, StubSection.Name, 0, """[{"id":"a"}]""", hash: new string('0', 64));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ImportCodes.HashMismatch, await ImportClient.CodeOf(response));
    }

    [Fact]
    public async Task A_refused_entity_comes_back_with_its_global_path_and_leaves_the_slot_free()
    {
        await using var app = await StartAsync();
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);

        var refused = await client.PutAsync(app.AdultA, session.Id, StubSection.Name, 2, """[{"id":"a"},{"id":"b","bad":true}]""");
        var corrected = await client.PutAsync(app.AdultA, session.Id, StubSection.Name, 2, """[{"id":"a"},{"id":"b"}]""");

        Assert.False(refused.Accepted);
        var issue = Assert.Single(refused.Issues);
        Assert.Equal("stub.bad", issue.Code);
        Assert.Equal("stub_things[1001].bad", issue.Path); // batch 2 of 500: entities 1000 onwards
        Assert.True(corrected.Accepted);
    }

    [Fact]
    public async Task Sections_are_known_or_refused_by_name()
    {
        await using var app = await StartAsync();
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);

        var unknown = await client.PutRawAsync(app.AdultA, session.Id, "gadgets", 0, "[]");
        var later = await client.PutRawAsync(app.AdultA, session.Id, "providers", 0, "[]");

        Assert.Equal(ImportCodes.UnknownSection, await ImportClient.CodeOf(unknown));
        Assert.Equal(ImportCodes.SectionNotSupported, await ImportClient.CodeOf(later));
    }

    [Fact]
    public async Task A_batch_over_500_entities_is_refused()
    {
        await using var app = await StartAsync();
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);

        var entities = "[" + string.Join(',', Enumerable.Range(0, 501).Select(i => $$"""{"id":"e{{i}}"}""")) + "]";
        var response = await client.PutRawAsync(app.AdultA, session.Id, StubSection.Name, 0, entities);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ImportCodes.TooManyEntities, await ImportClient.CodeOf(response));
    }

    [Fact]
    public async Task A_body_over_one_megabyte_is_refused_before_it_is_read_as_json()
    {
        await using var app = await StartAsync();
        var session = await new ImportClient(app).StartAsync(app.AdultA);

        var body = new string(' ', 1_048_577);
        var response = await app.RawTextAsync(app.AdultA, HttpMethod.Put, $"{ImportClient.Root}/{session.Id}/batches/{StubSection.Name}/0", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(29, true)]  // root object, entities array, entity object, 29 arrays: depth 32
    [InlineData(30, false)] // depth 33
    public async Task Json_deeper_than_32_levels_is_refused(int arrays, bool accepted)
    {
        await using var app = await StartAsync();
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);

        var entities = $$"""[{"id":"deep","nest":{{new string('[', arrays)}}{{new string(']', arrays)}}}]""";
        var response = await client.PutRawAsync(app.AdultA, session.Id, StubSection.Name, 0, entities);

        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_string_longer_than_the_bound_is_refused()
    {
        await using var app = await StartAsync();
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);

        var response = await client.PutRawAsync(app.AdultA, session.Id, StubSection.Name, 0, $$"""[{"id":"{{new string('x', 10_001)}}"}]""");

        Assert.Equal(ImportCodes.StringTooLong, await ImportClient.CodeOf(response));
    }

    [Fact]
    public async Task Another_person_cannot_send_batches_to_a_session()
    {
        await using var app = await StartAsync();
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);

        var response = await client.PutRawAsync(app.AdultB, session.Id, StubSection.Name, 0, """[{"id":"a"}]""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
