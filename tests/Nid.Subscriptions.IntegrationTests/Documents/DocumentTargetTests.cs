using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Documents.Application;
using Nid.Documents.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.Subscriptions.IntegrationTests.Documents.DocumentEndpointTests;

namespace Nid.Subscriptions.IntegrationTests.Documents;

/// <summary>Documents tranche 2 design §2.1: the targets the page offers, the preview of a link, the module capability.</summary>
public class DocumentTargetTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Root = DocumentEndpointTests.Documents;

    private static async Task<JsonElement[]> TargetsAsync(NidApp app, PersonId actor)
    {
        var response = await app.RawAsync(actor, HttpMethod.Get, $"{Root}/targets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("items").EnumerateArray().ToArray();
    }

    private static Task<HttpResponseMessage> PreviewAsync(NidApp app, PersonId actor, Guid id, string kind, Guid? target)
        => app.RawAsync(actor, HttpMethod.Get, $"{Root}/{id}/links/preview?targetKind={kind}" + (target is null ? "" : $"&targetId={target}"));

    private static (string Kind, Guid Id, string Label, bool CanWrite) Row(JsonElement t)
        => (t.GetProperty("kind").GetString()!, t.GetProperty("id").GetGuid(), t.GetProperty("label").GetString()!, t.GetProperty("canWrite").GetBoolean());

    [Fact]
    public async Task Lists_the_actors_items_the_persons_and_the_household_never_the_other_adults_private_item()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await app.NewSubscriptionAsync("Banque A", app.AdultA, Visibility.Private);
        var ours = await app.NewSubscriptionAsync("Box", app.AdultA, Visibility.Shared);
        var theirsShared = await app.NewSubscriptionAsync("Assurance", app.AdultB, Visibility.Shared);
        var secret = await app.NewSubscriptionAsync("CoffreSecretB", app.AdultB, Visibility.Private);
        await using (var core = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            core.Persons.Add(new PersonRow
            {
                Id = Guid.CreateVersion7(), Kind = "adult", DisplayName = "EnAttente", Email = "pending@test.localhost",
                CreatedAt = SystemClock.Instance.GetCurrentInstant(),
            });
            await core.SaveChangesAsync();
        }

        var raw = await app.GetRawAsync(app.AdultA, $"{Root}/targets");
        Assert.DoesNotContain("CoffreSecretB", raw);
        Assert.DoesNotContain(secret.Value.ToString(), raw);
        Assert.DoesNotContain("EnAttente", raw);

        var targets = await TargetsAsync(app, app.AdultA);
        var names = await PersonNamesAsync(app);
        var rows = targets.Select(Row).ToArray();
        // Items sorted by label, then the persons, then the household; the persons' order is not guaranteed (same CreatedAt, v7 ids).
        Assert.Equal(
            [
                (TargetKinds.CoreItem, theirsShared.Value, "Assurance", false),
                (TargetKinds.CoreItem, mine.Value, "Banque A", true),
                (TargetKinds.CoreItem, ours.Value, "Box", true),
            ],
            rows[..3]);
        Assert.Equivalent(
            new[]
            {
                (TargetKinds.CorePerson, app.AdultA.Value, names[app.AdultA.Value], true),
                (TargetKinds.CorePerson, app.AdultB.Value, names[app.AdultB.Value], false),
                (TargetKinds.CorePerson, app.Dependent.Value, names[app.Dependent.Value], true),
            },
            rows[3..6]);
        Assert.Equal((TargetKinds.CoreHousehold, TargetKinds.Household, "Foyer", true), rows[6]);
        Assert.Equal(7, rows.Length);
        var item = targets[0];
        Assert.Equal(("subscriptions", "subscription"), (item.GetProperty("module").GetString(), item.GetProperty("itemKind").GetString()));
        Assert.Equal(JsonValueKind.Null, targets[^1].GetProperty("module").ValueKind);

        // B reads A's shared item without writing it.
        Assert.Contains((TargetKinds.CoreItem, ours.Value, "Box", false), (await TargetsAsync(app, app.AdultB)).Select(Row));
    }

    private static async Task<Dictionary<Guid, string>> PersonNamesAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IPersonRegistry>().ListAsync(default)).ToDictionary(p => p.Id.Value, p => p.DisplayName);
    }

    [Fact]
    public async Task Previews_the_readers_a_link_would_add_and_writes_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Box", app.AdultA, Visibility.Shared);
        var mine = await app.NewSubscriptionAsync("Banque", app.AdultA, Visibility.Private);
        var id = await UploadedAsync(app, app.AdultA, Pdf);

        var response = await PreviewAsync(app, app.AdultA, id, TargetKinds.CoreItem, shared.Value);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var gained = Assert.Single((await JsonAsync(response)).GetProperty("gainedReaders").EnumerateArray());
        Assert.Equal((app.AdultB.Value, (await PersonNamesAsync(app))[app.AdultB.Value]),
            (gained.GetProperty("id").GetGuid(), gained.GetProperty("name").GetString()));
        Assert.Equal(0, (await JsonAsync(await GetAsync(app, app.AdultA, id))).GetProperty("document").GetProperty("links").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(app, app.AdultB, id)).StatusCode);

        var none = await PreviewAsync(app, app.AdultA, id, TargetKinds.CoreItem, mine.Value);
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        Assert.Equal(0, (await JsonAsync(none)).GetProperty("gainedReaders").GetArrayLength());

        var household = await PreviewAsync(app, app.AdultA, id, TargetKinds.CoreHousehold, null);
        Assert.Equal(HttpStatusCode.OK, household.StatusCode);
        Assert.Single((await JsonAsync(household)).GetProperty("gainedReaders").EnumerateArray());
    }

    [Fact]
    public async Task Refuses_a_preview_exactly_as_the_link()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Box", app.AdultA, Visibility.Shared);
        var theirs = await app.NewSubscriptionAsync("Banque B", app.AdultB, Visibility.Private);
        var secret = await app.NewSubscriptionAsync("Banque A", app.AdultA, Visibility.Private);
        var hidden = await UploadedAsync(app, app.AdultA, Pdf);
        var readOnly = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&targetKind=core.item&targetId={shared.Value}&role=invoice");

        // A document B does not read, and an item A does not read: as missing ones.
        Assert.Equal(HttpStatusCode.NotFound, (await PreviewAsync(app, app.AdultB, hidden, TargetKinds.CoreItem, theirs.Value)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PreviewAsync(app, app.AdultA, hidden, TargetKinds.CoreItem, theirs.Value)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PreviewAsync(app, app.AdultA, hidden, TargetKinds.CorePerson, app.AdultB.Value)).StatusCode);
        // B reads the document but edits neither it nor the shared item.
        Assert.Equal(HttpStatusCode.Forbidden, (await PreviewAsync(app, app.AdultB, readOnly, TargetKinds.CoreItem, theirs.Value)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PreviewAsync(app, app.AdultB, readOnly, TargetKinds.CoreHousehold, null)).StatusCode);
        await AssertRefusedAsync(await PreviewAsync(app, app.AdultA, readOnly, TargetKinds.CoreItem, shared.Value), HttpStatusCode.Conflict, DocumentsCodes.AlreadyLinked);
        await AssertRefusedAsync(await PreviewAsync(app, app.AdultA, readOnly, "nope", Guid.NewGuid()), HttpStatusCode.BadRequest, DocumentsCodes.UnknownTargetKind);
        Assert.Equal(HttpStatusCode.OK, (await PreviewAsync(app, app.AdultA, readOnly, TargetKinds.CoreItem, secret.Value)).StatusCode);

        await using var scope = app.Services.CreateAsyncScope();
        var links = await scope.ServiceProvider.GetRequiredService<IDocumentStore>().LinksOfAsync([hidden, readOnly], default);
        Assert.Equal([shared.Value], links.Select(l => l.TargetId));
    }

    [Fact]
    public async Task The_capabilities_list_the_documents_module()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var capabilities = await app.GetAsync<JsonElement>(app.AdultA, "/api/v1/platform/capabilities");

        Assert.Contains("documents", capabilities.GetProperty("modules").EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public async Task Refuses_a_multipart_body_without_a_boundary()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var content = new ByteArrayContent(Pdf);
        content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");

        var response = await app.RawContentAsync(app.AdultA, HttpMethod.Post, Root, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Refuses_an_unreadable_target_id()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await UploadAsync(app, app.AdultA, Pdf, "fileName=a.pdf&targetKind=core.item&targetId=not-a-guid&role=other");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Root}?scope=me"))).GetProperty("items").GetArrayLength());
    }
}
