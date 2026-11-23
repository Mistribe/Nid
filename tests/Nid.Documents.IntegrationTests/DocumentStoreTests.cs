using Microsoft.Extensions.DependencyInjection;
using Nid.Documents.Application;
using Nid.Documents.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Documents.IntegrationTests;

public class DocumentStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Instant At = Instant.FromUtc(2026, 10, 26, 9, 0);

    private static DocumentRecord Doc(Guid? id = null, Instant? archived = null)
        => new(id ?? Guid.CreateVersion7(), new string('a', 64), 1234, "application/pdf", "bail.pdf", "Bail",
            new LocalDate(2026, 9, 1), "une note", Guid.CreateVersion7(), At, archived);

    private static LinkRecord Link(Guid doc, DocumentTarget t, string role = "lease")
        => new(doc, t.Kind, t.Id, null, role, Guid.CreateVersion7(), At);

    private async Task<(NidApp App, AsyncServiceScope Scope, IDocumentStore Store)> StartAsync()
    {
        var app = await NidApp.StartAsync(fixture);
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return (app, scope, scope.ServiceProvider.GetRequiredService<IDocumentStore>());
    }

    [Fact]
    public async Task Create_then_find_round_trips_every_field()
    {
        var (app, scope, store) = await StartAsync();
        await using var _ = app;
        await using var __ = scope;
        var doc = Doc();

        await store.CreateAsync(doc, TestContext.Current.CancellationToken);

        var found = await store.FindAsync(doc.Id, TestContext.Current.CancellationToken);
        // The version is the row's xmin, set by the database.
        Assert.Equal(doc, found! with { Version = 0 });
        Assert.NotEqual(0u, found.Version);
        Assert.Null(await store.FindAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unanchored_link_of_the_actor_s_own_counts_on_its_kind_and_id_both()
    {
        var (app, scope, store) = await StartAsync();
        await using var _ = app;
        await using var __ = scope;
        var ct = TestContext.Current.CancellationToken;
        var doc = Doc();
        var line = new DocumentTarget("banking.transaction", Guid.NewGuid());
        await store.CreateAsync(doc, ct);
        await store.AddLinkAsync(Link(doc.Id, line, "receipt"), ct);
        async Task<bool> InboxAsync(DocumentTarget own) => (await store.PageAsync(new DocumentPageQuery(doc.UploadedBy, [], [], [], false, null, true, null, null,
            null, null, null, null, null, DateTimeZone.Utc, 0, null, [own]), ct)).Items.Any(d => d.Id == doc.Id);

        Assert.False(await InboxAsync(line));
        Assert.True(await InboxAsync(line with { Kind = "income.phase" }));
    }

    [Fact]
    public async Task Add_link_twice_returns_false_the_second_time()
    {
        var (app, scope, store) = await StartAsync();
        await using var _ = app;
        await using var __ = scope;
        var doc = Doc();
        await store.CreateAsync(doc, TestContext.Current.CancellationToken);
        var target = new DocumentTarget(TargetKinds.CoreItem, Guid.NewGuid());

        Assert.True(await store.AddLinkAsync(Link(doc.Id, target), TestContext.Current.CancellationToken));
        Assert.False(await store.AddLinkAsync(Link(doc.Id, target), TestContext.Current.CancellationToken));
        Assert.Single(await store.LinksOfAsync([doc.Id], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Moving_links_onto_the_same_target_keeps_them()
    {
        var (app, scope, store) = await StartAsync();
        await using var _ = app;
        await using var __ = scope;
        var doc = Doc();
        await store.CreateAsync(doc, TestContext.Current.CancellationToken);
        var target = new DocumentTarget(TargetKinds.CoreItem, Guid.NewGuid());
        await store.AddLinkAsync(Link(doc.Id, target), TestContext.Current.CancellationToken);

        await store.MoveLinksAsync(target, target, TestContext.Current.CancellationToken);

        Assert.Single(await store.LinksOfAsync([doc.Id], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Set_role_and_remove_link_on_a_missing_link_return_false()
    {
        var (app, scope, store) = await StartAsync();
        await using var _ = app;
        await using var __ = scope;
        var doc = Doc();
        await store.CreateAsync(doc, TestContext.Current.CancellationToken);
        var target = new DocumentTarget(TargetKinds.CorePerson, Guid.NewGuid());

        Assert.False(await store.SetRoleAsync(doc.Id, target, "deed", TestContext.Current.CancellationToken));
        Assert.False(await store.RemoveLinkAsync(doc.Id, target, TestContext.Current.CancellationToken));

        await store.AddLinkAsync(Link(doc.Id, target), TestContext.Current.CancellationToken);
        Assert.True(await store.SetRoleAsync(doc.Id, target, "deed", TestContext.Current.CancellationToken));
        Assert.Equal("deed", Assert.Single(await store.LinksOfAsync([doc.Id], TestContext.Current.CancellationToken)).Role);
        Assert.True(await store.RemoveLinkAsync(doc.Id, target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Remove_links_to_a_target_leaves_the_documents()
    {
        var (app, scope, store) = await StartAsync();
        await using var _ = app;
        await using var __ = scope;
        var one = Doc();
        var two = Doc();
        await store.CreateAsync(one, TestContext.Current.CancellationToken);
        await store.CreateAsync(two, TestContext.Current.CancellationToken);
        var target = new DocumentTarget(TargetKinds.CoreItem, Guid.NewGuid());
        var other = new DocumentTarget(TargetKinds.CoreHousehold, TargetKinds.Household);
        await store.AddLinkAsync(Link(one.Id, target), TestContext.Current.CancellationToken);
        await store.AddLinkAsync(Link(two.Id, target), TestContext.Current.CancellationToken);
        await store.AddLinkAsync(Link(one.Id, other), TestContext.Current.CancellationToken);

        Assert.Equal(2, await store.RemoveLinksToAsync(target, TestContext.Current.CancellationToken));

        Assert.Equal(other.Kind, Assert.Single(await store.LinksOfAsync([one.Id, two.Id], TestContext.Current.CancellationToken)).TargetKind);
        Assert.NotNull(await store.FindAsync(one.Id, TestContext.Current.CancellationToken));
        Assert.NotNull(await store.FindAsync(two.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Move_targets_rewrites_only_the_links_to_the_source_and_keeps_one_link_per_document()
    {
        var (app, scope, store) = await StartAsync();
        await using var _ = app;
        await using var __ = scope;
        var (one, two, three) = (Doc(), Doc(), Doc());
        foreach (var d in new[] { one, two, three })
            await store.CreateAsync(d, TestContext.Current.CancellationToken);
        var from = new DocumentTarget("income.one_off", Guid.NewGuid());
        var to = new DocumentTarget("income.one_off", Guid.NewGuid());
        var unrelated = new DocumentTarget("income.one_off", Guid.NewGuid());
        var anchor = Guid.NewGuid();
        await store.AddLinkAsync(Link(one.Id, from) with { AnchorItemId = anchor, Role = "deed" }, TestContext.Current.CancellationToken);
        await store.AddLinkAsync(Link(two.Id, from), TestContext.Current.CancellationToken);
        await store.AddLinkAsync(Link(two.Id, to, "invoice"), TestContext.Current.CancellationToken); // already linked to the destination
        await store.AddLinkAsync(Link(three.Id, unrelated), TestContext.Current.CancellationToken);

        await scope.ServiceProvider.GetRequiredService<IDocumentLinks>().MoveTargetAsync(from, to, TestContext.Current.CancellationToken);

        var links = await store.LinksOfAsync([one.Id, two.Id, three.Id], TestContext.Current.CancellationToken);
        var moved = Assert.Single(links, l => l.DocumentId == one.Id);
        Assert.Equal((to.Kind, to.Id, anchor, "deed"), (moved.TargetKind, moved.TargetId, moved.AnchorItemId, moved.Role));
        var kept = Assert.Single(links, l => l.DocumentId == two.Id);
        Assert.Equal((to.Id, "invoice"), (kept.TargetId, kept.Role));
        Assert.Equal(unrelated.Id, Assert.Single(links, l => l.DocumentId == three.Id).TargetId);
        Assert.DoesNotContain(links, l => l.TargetId == from.Id);
    }

    [Fact]
    public async Task List_hides_archived_unless_asked()
    {
        var (app, scope, store) = await StartAsync();
        await using var _ = app;
        await using var __ = scope;
        var live = Doc();
        var archived = Doc(archived: At);
        await store.CreateAsync(live, TestContext.Current.CancellationToken);
        await store.CreateAsync(archived, TestContext.Current.CancellationToken);

        var visible = await store.ListAsync(false, TestContext.Current.CancellationToken);
        Assert.Contains(visible, d => d.Id == live.Id);
        Assert.DoesNotContain(visible, d => d.Id == archived.Id);
        Assert.Contains(await store.ListAsync(true, TestContext.Current.CancellationToken), d => d.Id == archived.Id);
    }
}
