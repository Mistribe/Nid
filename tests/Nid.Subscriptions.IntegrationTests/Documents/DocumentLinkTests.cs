using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.Documents.DocumentEndpointTests;

namespace Nid.Subscriptions.IntegrationTests.Documents;

/// <summary>Documents design §7: links, gained readers, the scoped list and « À classer ».</summary>
public class DocumentLinkTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Root = DocumentEndpointTests.Documents;

    private static Task<HttpResponseMessage> LinkAsync(NidApp app, PersonId actor, Guid id, string kind, Guid? target, string role = "other")
        => app.RawAsync(actor, HttpMethod.Post, $"{Root}/{id}/links", new { targetKind = kind, targetId = target, role });

    private static async Task<Guid[]> GainedAsync(NidApp app, PersonId actor, Guid id, string kind, Guid? target, string role = "other")
    {
        var response = await LinkAsync(app, actor, id, kind, target, role);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("gainedReaders").EnumerateArray().Select(g => g.GetGuid()).ToArray();
    }

    private static Task<HttpResponseMessage> UnlinkAsync(NidApp app, PersonId actor, Guid id, string kind, Guid target)
        => app.RawAsync(actor, HttpMethod.Delete, $"{Root}/{id}/links/{kind}/{target}");

    private static async Task<Guid[]> ListedAsync(NidApp app, PersonId actor, string query = "")
    {
        var response = await app.RawAsync(actor, HttpMethod.Get, $"{Root}?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("items").EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ToArray();
    }

    private static async Task<JsonElement[]> LinksAsync(NidApp app, PersonId actor, Guid id)
        => (await JsonAsync(await GetAsync(app, actor, id))).GetProperty("document").GetProperty("links").EnumerateArray().ToArray();

    [Fact]
    public async Task A_link_to_a_shared_item_names_the_reader_it_gains()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Box", app.AdultA, Visibility.Shared);
        var mine = await app.NewSubscriptionAsync("Banque", app.AdultA, Visibility.Private);
        var id = await UploadedAsync(app, app.AdultA, Pdf);
        var other = await UploadedAsync(app, app.AdultA, Pdf);

        Assert.Equal([app.AdultB.Value], await GainedAsync(app, app.AdultA, id, TargetKinds.CoreItem, shared.Value, "deed"));
        Assert.Contains(id, await ListedAsync(app, app.AdultB));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, app.AdultB, id)).StatusCode);

        Assert.Empty(await GainedAsync(app, app.AdultA, other, TargetKinds.CoreItem, mine.Value));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(app, app.AdultB, other)).StatusCode);
    }

    [Fact]
    public async Task The_other_adults_private_link_appears_nowhere()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Box", app.AdultB, Visibility.Shared);
        var secret = await app.NewSubscriptionAsync("CoffreSecretB", app.AdultB, Visibility.Private);
        var id = await UploadedAsync(app, app.AdultB, Pdf, $"fileName=a.pdf&targetKind=core.item&targetId={shared.Value}&role=invoice");
        await GainedAsync(app, app.AdultB, id, TargetKinds.CoreItem, secret.Value);

        var detail = await (await GetAsync(app, app.AdultA, id)).Content.ReadAsStringAsync();
        var list = await (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Root}?scope=me")).Content.ReadAsStringAsync();
        foreach (var body in new[] { detail, list })
        {
            Assert.Contains(shared.Value.ToString(), body);
            Assert.DoesNotContain("CoffreSecretB", body);
            Assert.DoesNotContain(secret.Value.ToString(), body);
        }

        var link = Assert.Single(await LinksAsync(app, app.AdultA, id));
        Assert.Equal(shared.Value, link.GetProperty("targetId").GetGuid());
        Assert.Equal(2, (await LinksAsync(app, app.AdultB, id)).Length);

        // A link the actor cannot see answers as one that does not exist.
        Assert.Equal(await RefusalAsync(UnlinkAsync(app, app.AdultA, id, TargetKinds.CoreItem, Guid.NewGuid())),
            await RefusalAsync(UnlinkAsync(app, app.AdultA, id, TargetKinds.CoreItem, secret.Value)));
        Assert.Equal(await RefusalAsync(app.RawAsync(app.AdultA, HttpMethod.Put, $"{Root}/{id}/links/{TargetKinds.CoreItem}/{Guid.NewGuid()}", new { role = "deed" })),
            await RefusalAsync(app.RawAsync(app.AdultA, HttpMethod.Put, $"{Root}/{id}/links/{TargetKinds.CoreItem}/{secret.Value}", new { role = "deed" })));
        Assert.Equal(2, (await LinksAsync(app, app.AdultB, id)).Length);
    }

    [Fact]
    public async Task A_person_link_the_actor_cannot_see_answers_as_a_missing_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await UploadedAsync(app, app.AdultB, Pdf, "fileName=a.pdf&targetKind=core.household&role=other");
        await GainedAsync(app, app.AdultB, id, TargetKinds.CorePerson, app.AdultB.Value, "payslip");

        var missing = await RefusalAsync(UnlinkAsync(app, app.AdultA, id, TargetKinds.CorePerson, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
        Assert.Equal(missing, await RefusalAsync(UnlinkAsync(app, app.AdultA, id, TargetKinds.CorePerson, app.AdultB.Value)));
        Assert.Equal(missing, await RefusalAsync(app.RawAsync(app.AdultA, HttpMethod.Put,
            $"{Root}/{id}/links/{TargetKinds.CorePerson}/{app.AdultB.Value}", new { role = "deed" })));
        Assert.Equal(2, (await LinksAsync(app, app.AdultB, id)).Length);
        Assert.Single(await LinksAsync(app, app.AdultA, id));
    }

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(Task<HttpResponseMessage> request)
    {
        var response = await request;
        return (response.StatusCode, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_reader_who_cannot_edit_cannot_link_the_document_to_their_own_items()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Box", app.AdultA, Visibility.Shared);
        var theirs = await app.NewSubscriptionAsync("Banque B", app.AdultB, Visibility.Private);
        var id = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&targetKind=core.item&targetId={shared.Value}&role=invoice");

        Assert.Equal(HttpStatusCode.Forbidden, (await LinkAsync(app, app.AdultB, id, TargetKinds.CoreItem, theirs.Value)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await LinkAsync(app, app.AdultB, id, TargetKinds.CoreHousehold, null)).StatusCode);

        Assert.Single(await LinksAsync(app, app.AdultB, id));
        Assert.Single(await LinksAsync(app, app.AdultA, id));
    }

    [Fact]
    public async Task Links_to_the_household_and_to_a_dependent_open_the_document_to_every_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var household = await UploadedAsync(app, app.AdultA, Pdf);
        var dependent = await UploadedAsync(app, app.AdultA, Pdf);
        var person = await UploadedAsync(app, app.AdultA, Pdf);

        Assert.Equal([app.AdultB.Value], await GainedAsync(app, app.AdultA, household, TargetKinds.CoreHousehold, null, "tax_notice"));
        Assert.Equal([app.AdultB.Value], await GainedAsync(app, app.AdultA, dependent, TargetKinds.CorePerson, app.Dependent.Value, "payslip"));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, app.AdultB, household)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, app.AdultB, dependent)).StatusCode);

        // Another adult's person is not A's to link to; it answers as a target that does not exist (controller ruling).
        await AssertRefusedAsync(await LinkAsync(app, app.AdultA, person, TargetKinds.CorePerson, app.AdultB.Value),
            HttpStatusCode.NotFound, DocumentsCodes.TargetNotFound);
        Assert.Empty(await LinksAsync(app, app.AdultA, person));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(app, app.AdultB, person)).StatusCode);
    }

    [Fact]
    public async Task Refuses_an_unknown_kind_a_missing_target_and_a_second_link()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Box", app.AdultA, Visibility.Shared);
        var id = await UploadedAsync(app, app.AdultA, Pdf);

        await AssertRefusedAsync(await LinkAsync(app, app.AdultA, id, "nope", Guid.NewGuid()), HttpStatusCode.BadRequest, DocumentsCodes.UnknownTargetKind);
        Assert.Equal(HttpStatusCode.NotFound, (await LinkAsync(app, app.AdultA, id, TargetKinds.CoreItem, Guid.NewGuid())).StatusCode);
        await GainedAsync(app, app.AdultA, id, TargetKinds.CoreItem, shared.Value);
        await AssertRefusedAsync(await LinkAsync(app, app.AdultA, id, TargetKinds.CoreItem, shared.Value), HttpStatusCode.Conflict, DocumentsCodes.AlreadyLinked);
        Assert.Single(await LinksAsync(app, app.AdultA, id));
    }

    [Fact]
    public async Task Unlinking_the_only_shared_link_takes_the_document_from_the_reader_but_not_from_its_uploader()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Box", app.AdultA, Visibility.Shared);
        var id = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&targetKind=core.item&targetId={shared.Value}&role=invoice");
        Assert.DoesNotContain(id, await ListedAsync(app, app.AdultA, "inbox=true"));

        Assert.Equal(HttpStatusCode.OK, (await UnlinkAsync(app, app.AdultA, id, TargetKinds.CoreItem, shared.Value)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, app.AdultA, id)).StatusCode);
        Assert.Contains(id, await ListedAsync(app, app.AdultA, "inbox=true"));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(app, app.AdultB, id)).StatusCode);
        Assert.DoesNotContain(id, await ListedAsync(app, app.AdultB, "scope=me"));
    }

    [Fact]
    public async Task The_uploader_who_sees_no_link_left_finds_the_document_in_the_inbox()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var joint = await app.NewOwnedSubscriptionAsync("Maison", Visibility.Shared, (app.AdultA, 5_000), (app.AdultB, 5_000));
        var theirs = await app.NewSubscriptionAsync("Banque B", app.AdultB, Visibility.Private);
        var id = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&targetKind=core.item&targetId={joint.Value}&role=deed");
        await GainedAsync(app, app.AdultB, id, TargetKinds.CoreItem, theirs.Value);

        Assert.Equal(HttpStatusCode.OK, (await UnlinkAsync(app, app.AdultA, id, TargetKinds.CoreItem, joint.Value)).StatusCode);

        Assert.Empty(await LinksAsync(app, app.AdultA, id));
        Assert.Contains(id, await ListedAsync(app, app.AdultA, "inbox=true"));
        Assert.DoesNotContain(id, await ListedAsync(app, app.AdultB, "inbox=true"));
        Assert.Single(await LinksAsync(app, app.AdultB, id));
    }

    [Fact]
    public async Task The_household_scope_keeps_documents_linked_to_shared_targets()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await app.NewSubscriptionAsync("Banque", app.AdultA, Visibility.Private);
        var shared = await app.NewSubscriptionAsync("Box", app.AdultA, Visibility.Shared);
        var priv = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&targetKind=core.item&targetId={mine.Value}&role=statement");
        var open = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=b.pdf&targetKind=core.item&targetId={shared.Value}&role=invoice");
        var household = await UploadedAsync(app, app.AdultA, Pdf, "fileName=c.pdf&targetKind=core.household&role=tax_notice");
        var inbox = await UploadedAsync(app, app.AdultA, Pdf);

        var scoped = await ListedAsync(app, app.AdultA, "scope=household");
        Assert.Equal(new[] { open, household }.Order(), scoped.Order());
        var me = await ListedAsync(app, app.AdultA, "scope=me");
        Assert.Equal(new[] { priv, open, household, inbox }.Order(), me.Order());
        Assert.Equal([inbox], await ListedAsync(app, app.AdultA, "inbox=true"));
    }

    [Fact]
    public async Task Lists_newest_document_date_first_and_hides_archived_rows()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var undated = await UploadedAsync(app, app.AdultA, Pdf);
        var old = await UploadedAsync(app, app.AdultA, Pdf, "fileName=a.pdf&date=2024-01-01");
        var recent = await UploadedAsync(app, app.AdultA, Pdf, "fileName=a.pdf&date=2025-06-01");
        var archived = await UploadedAsync(app, app.AdultA, Pdf, "fileName=a.pdf&date=2025-01-01");
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Root}/{archived}/archive");

        Assert.Equal([recent, old, undated], await ListedAsync(app, app.AdultA, "scope=me"));
        Assert.Equal([recent, archived, old, undated], await ListedAsync(app, app.AdultA, "scope=me&archived=true"));
    }

    [Fact]
    public async Task Changes_the_role_of_a_link()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Box", app.AdultA, Visibility.Shared);
        var id = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&targetKind=core.item&targetId={shared.Value}&role=invoice");
        var url = $"{Root}/{id}/links/{TargetKinds.CoreItem}/{shared.Value}";

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, url, new { role = "lease" })).StatusCode);
        Assert.Equal("lease", Assert.Single(await LinksAsync(app, app.AdultA, id)).GetProperty("role").GetString());

        await AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Put, url, new { role = "nope" }), HttpStatusCode.BadRequest, DocumentsCodes.InvalidRole);
        // B reads the link through the shared item but does not write it.
        Assert.Equal(HttpStatusCode.Forbidden, (await app.RawAsync(app.AdultB, HttpMethod.Put, url, new { role = "deed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await UnlinkAsync(app, app.AdultB, id, TargetKinds.CoreItem, shared.Value)).StatusCode);
        Assert.Equal("lease", Assert.Single(await LinksAsync(app, app.AdultA, id)).GetProperty("role").GetString());
    }
}
