using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Documents.Application;
using Nid.Documents.Contracts;
using Nid.Documents.Infrastructure.Persistence;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Documents.IntegrationTests;

/// <summary>#23: an item delete unlinks its documents, and only unlinks them.</summary>
public class DocumentsDeletionPartTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Instant At = Instant.FromUtc(2026, 10, 26, 9, 0);

    [Fact]
    public async Task Counts_and_removes_item_and_anchored_links()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = sp.GetRequiredService<IDocumentStore>();
        var item = Guid.NewGuid();
        var elsewhere = Guid.NewGuid();
        var (onItem, anchored, other) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        foreach (var id in new[] { onItem, anchored, other })
            await store.CreateAsync(new DocumentRecord(id, new string('a', 64), 1234, "application/pdf", "bail.pdf", "Bail", new LocalDate(2026, 9, 1), null, Guid.CreateVersion7(), At, null), ct);
        await store.AddLinkAsync(new LinkRecord(onItem, TargetKinds.CoreItem, item, null, "lease", Guid.CreateVersion7(), At), ct);
        await store.AddLinkAsync(new LinkRecord(anchored, "realestate.loan", Guid.NewGuid(), item, "loan_offer", Guid.CreateVersion7(), At), ct);
        await store.AddLinkAsync(new LinkRecord(other, TargetKinds.CoreItem, elsewhere, null, "lease", Guid.CreateVersion7(), At), ct);

        var db = sp.GetRequiredService<DocumentsDbContext>();
        var part = new DocumentsDeletionPart(db);
        var access = new ItemAccess(new ItemId(item), "Appartement", Visibility.Shared, []);
        var count = Assert.Single(await part.CountAsync(access, ct));
        Assert.Equal(("documents", 2), (count.Key, count.Count));

        await part.RemoveAsync(access, ct);

        Assert.Equal(0, Assert.Single(await part.CountAsync(access, ct)).Count);
        Assert.Equal(elsewhere, Assert.Single(await db.Links.AsNoTracking().ToListAsync(ct)).TargetId);
        Assert.Equal(3, await db.Documents.CountAsync(ct));
    }
}
