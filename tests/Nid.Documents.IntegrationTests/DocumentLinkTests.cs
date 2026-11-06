using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Documents.Application;
using Nid.Documents.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.DocumentApi;

namespace Nid.Documents.IntegrationTests;

/// <summary>Documents design §7: links, gained readers, the scoped list and « À classer ».</summary>
public class DocumentLinkTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Root = DocumentApi.Documents;

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

        var detail = await (await GetAsync(app, app.AdultA, id)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var list = await (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Root}?scope=me")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
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

    [Fact]
    public async Task The_list_by_target_keeps_the_documents_linked_to_it_and_hides_a_target_the_reader_cannot_see()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Box", app.AdultA, Visibility.Shared);
        var secret = await app.NewSubscriptionAsync("CoffreB", app.AdultB, Visibility.Private);
        var linked = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&targetKind=core.item&targetId={shared.Value}&role=invoice");
        var elsewhere = await UploadedAsync(app, app.AdultA, Pdf);
        var hidden = await UploadedAsync(app, app.AdultB, Pdf, $"fileName=b.pdf&targetKind=core.item&targetId={secret.Value}&role=invoice");

        Assert.Equal([linked], await ListedAsync(app, app.AdultA, $"scope=me&targetKind=core.item&targetId={shared.Value}"));
        Assert.Empty(await ListedAsync(app, app.AdultA, $"scope=me&targetKind=core.item&targetId={secret.Value}"));
        Assert.Empty(await ListedAsync(app, app.AdultA, $"scope=me&targetKind=core.item&targetId={Guid.NewGuid()}"));
        Assert.Equal([hidden], await ListedAsync(app, app.AdultB, $"scope=me&targetKind=core.item&targetId={secret.Value}"));
        Assert.DoesNotContain(elsewhere, await ListedAsync(app, app.AdultA, $"targetKind=core.item&targetId={shared.Value}"));
    }

    [Fact]
    public async Task A_link_to_a_target_nobody_resolves_any_more_is_labelled_deleted_and_still_counts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Box", app.AdultA, Visibility.Shared);
        var id = await UploadedAsync(app, app.AdultA, Pdf);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<IDocumentStore>().AddLinkAsync(
                new LinkRecord(id, "income.phase", Guid.NewGuid(), shared.Value, "other", app.AdultA.Value, SystemClock.Instance.GetCurrentInstant()), TestContext.Current.CancellationToken);
        }

        var link = Assert.Single(await LinksAsync(app, app.AdultA, id));
        Assert.Equal("Élément supprimé", link.GetProperty("label").GetString());
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, app.AdultB, id)).StatusCode); // the shared anchor still lets B read it
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

    /// <summary>Issue #420: type, year, linked element and title search apply on the server; the total counts what matched, the facets every listed document.</summary>
    [Fact]
    public async Task Filters_searches_and_pages_on_the_server()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var box = await app.NewSubscriptionAsync("Box", app.AdultA, Visibility.Shared);
        var invoice = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&title=Facture%20box&date=2025-06-10&targetKind=core.item&targetId={box.Value}&role=invoice");
        var lease = await UploadedAsync(app, app.AdultA, Pdf, "fileName=b.pdf&title=Bail%20Lyon&date=2024-03-01&targetKind=core.household&role=lease");
        var tax = await UploadedAsync(app, app.AdultA, Pdf, "fileName=c.pdf&title=Avis%20imposition&date=2025-09-01&targetKind=core.household&role=tax_notice");

        Assert.Equal([invoice], await ListedAsync(app, app.AdultA, "role=invoice"));
        Assert.Equal([tax, invoice], await ListedAsync(app, app.AdultA, "year=2025"));
        Assert.Equal([invoice], await ListedAsync(app, app.AdultA, $"linked={box.Value}"));
        Assert.Equal([lease], await ListedAsync(app, app.AdultA, "q=bAIL"));
        Assert.Empty(await ListedAsync(app, app.AdultA, "role=invoice&year=2024"));

        var first = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Root}?year=2025&page=1"));
        Assert.Equal(2, first.GetProperty("total").GetInt32());
        var facets = first.GetProperty("facets");
        Assert.Equal(["invoice", "lease", "tax_notice"], facets.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal([2025, 2024], facets.GetProperty("years").EnumerateArray().Select(y => y.GetInt32()));
        Assert.Equal(new[] { box.Value, TargetKinds.Household }.Order(), facets.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("targetId").GetGuid()).Order());

        // Undated: its upload day in the household's zone counts (the test clock uploads on 2025-10-15), and it sorts last.
        var scan = await UploadedAsync(app, app.AdultA, Pdf, "fileName=d.pdf&title=Scan%2050%25");
        Assert.Equal([tax, invoice, scan], await ListedAsync(app, app.AdultA, "scope=me&year=2025"));
        // Typed text, not a pattern: % matches only itself.
        Assert.Equal([scan], await ListedAsync(app, app.AdultA, "scope=me&q=%25"));
        Assert.Empty(await ListedAsync(app, app.AdultA, "scope=me&q=_"));

        // Past the last page: no row, the same total.
        var past = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Root}?year=2025&page=2"));
        Assert.Empty(past.GetProperty("items").EnumerateArray());
        Assert.Equal(2, past.GetProperty("total").GetInt32());
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

    /// <summary>Syndic documents design §2.2 and issue #304: the roles added after the first ones.</summary>
    [Theory]
    [InlineData("copro_call")]
    [InlineData("copro_minutes")]
    [InlineData("copro_accounts")]
    // Issue #304: the investment papers.
    [InlineData("contract")]
    [InlineData("transaction_notice")]
    [InlineData("request")]
    [InlineData("advice")]
    [InlineData("product_sheet")]
    public async Task Links_with_each_added_role(string role)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await app.NewSubscriptionAsync("Box", app.AdultA, Visibility.Shared);
        var id = await UploadedAsync(app, app.AdultA, Pdf);

        Assert.Equal(HttpStatusCode.OK, (await LinkAsync(app, app.AdultA, id, TargetKinds.CoreItem, shared.Value, role)).StatusCode);
        Assert.Equal(role, Assert.Single(await LinksAsync(app, app.AdultA, id)).GetProperty("role").GetString());
    }

    /// <summary>Issue #183: « Classer » links each selected document on its own, so a refused one leaves the others filed.</summary>
    [Fact]
    public async Task Filing_several_documents_links_each_one_and_a_refused_one_leaves_the_others_filed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var box = await app.NewSubscriptionAsync("Box", app.AdultA, Visibility.Private);
        var shared = await app.NewSubscriptionAsync("Partagé", app.AdultB, Visibility.Shared);
        var first = await UploadedAsync(app, app.AdultA, Pdf);
        var second = await UploadedAsync(app, app.AdultA, Pdf);
        var already = await UploadedAsync(app, app.AdultA, Pdf, $"fileName=a.pdf&targetKind=core.item&targetId={box.Value}&role=invoice");
        // A reads B's document through the shared item, but cannot edit it.
        var theirs = await UploadedAsync(app, app.AdultB, Pdf, $"fileName=b.pdf&targetKind=core.item&targetId={shared.Value}&role=invoice");
        var before = await ListedAsync(app, app.AdultA, "inbox=true");
        Assert.Contains(first, before);
        Assert.Contains(second, before);

        var answers = new List<HttpResponseMessage>();
        foreach (var id in new[] { first, theirs, second, already })
            answers.Add(await LinkAsync(app, app.AdultA, id, TargetKinds.CoreItem, box.Value, "payslip"));

        Assert.Equal(HttpStatusCode.OK, answers[0].StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, answers[1].StatusCode);
        Assert.Equal(HttpStatusCode.OK, answers[2].StatusCode);
        await AssertRefusedAsync(answers[3], HttpStatusCode.Conflict, DocumentsCodes.AlreadyLinked);

        var inbox = await ListedAsync(app, app.AdultA, "inbox=true");
        Assert.DoesNotContain(first, inbox);
        Assert.DoesNotContain(second, inbox);
        foreach (var id in new[] { first, second })
            Assert.Equal("payslip", Assert.Single(await LinksAsync(app, app.AdultA, id)).GetProperty("role").GetString());
        Assert.Single(await LinksAsync(app, app.AdultB, theirs));
        Assert.Equal("invoice", Assert.Single(await LinksAsync(app, app.AdultA, already)).GetProperty("role").GetString());
    }
}
