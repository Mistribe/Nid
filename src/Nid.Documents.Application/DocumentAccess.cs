using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.Documents.Domain;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Documents.Application;

/// <summary>A document the actor reads, with all its links (seen or not) and what the actor may do with them.</summary>
public sealed record ReadableDocument(DocumentRecord Document, IReadOnlyList<LinkRecord> Links, ActorFacts Actor)
{
    public bool CanEdit => DocumentRules.CanEdit(Document.UploadedBy, Links.Select(DocumentAccess.Facts), Actor);
}

/// <summary>
/// Design §7: who reads, edits and links a document. Every decision goes through <see cref="DocumentRules"/>, fed with the
/// item rights of <see cref="IAccessPolicy"/> on the links' anchors.
/// </summary>
public sealed class DocumentAccess(
    IAccessPolicy policy, IPersonRegistry persons, IEnumerable<IDocumentTargets> targets, IDocumentStore store, IClock clock)
{
    public static LinkFacts Facts(LinkRecord link) => new(link.TargetKind, link.TargetId, link.AnchorItemId);

    public async Task<ActorFacts> FactsAsync(PersonId actor, IReadOnlyCollection<Guid> anchors, CancellationToken ct)
    {
        var items = anchors.Distinct().Select(a => new ItemId(a)).ToList();
        var dependents = (await persons.ListAsync(ct)).Where(p => p.Kind == PersonKind.Dependent).Select(p => p.Id.Value).ToHashSet();
        if (items.Count == 0)
            return new ActorFacts(actor.Value, dependents, new HashSet<Guid>(), new HashSet<Guid>());

        var read = await policy.CheckManyAsync(actor, items, AccessKind.Read, ct);
        var write = await policy.CheckManyAsync(actor, items, AccessKind.Write, ct);
        return new ActorFacts(actor.Value, dependents, read.Select(i => i.Value).ToHashSet(), write.Select(i => i.Value).ToHashSet());
    }

    /// <summary>The adults who read a document uploaded by <paramref name="uploadedBy"/> with these links.</summary>
    public async Task<IReadOnlyList<PersonId>> ReadersAsync(Guid uploadedBy, IReadOnlyList<LinkFacts> links, CancellationToken ct)
    {
        var anchors = links.Where(l => l.AnchorItemId is not null).Select(l => l.AnchorItemId!.Value).ToList();
        var readers = new List<PersonId>();
        foreach (var adult in await persons.AdultIdsAsync(ct))
            if (DocumentRules.CanRead(uploadedBy, links, await FactsAsync(adult, anchors, ct)))
                readers.Add(adult);
        return readers;
    }

    /// <summary><c>NotFoundFailure</c> for a document that does not exist or that the actor does not read: the two are not told apart.</summary>
    public async Task<Result<ReadableDocument>> ReadAsync(Guid id, PersonId actor, CancellationToken ct)
    {
        if (await store.FindAsync(id, ct) is not { } document)
            return NotFound(id);
        var links = await store.LinksOfAsync([id], ct);
        var facts = await FactsAsync(actor, Anchors(links), ct);
        return DocumentRules.CanRead(document.UploadedBy, links.Select(Facts), facts)
            ? Result.Success(new ReadableDocument(document, links, facts))
            : NotFound(id);
    }

    /// <summary>As <see cref="ReadAsync"/>, then <c>UnauthorizedFailure</c> when the actor reads the document but may not edit it.</summary>
    public async Task<Result<ReadableDocument>> EditableAsync(Guid id, PersonId actor, CancellationToken ct)
    {
        var read = await ReadAsync(id, actor, ct);
        return read.TryGetValue(out var found) && !found!.CanEdit
            ? Result.Failure<ReadableDocument>(new UnauthorizedFailure("The document is read-only for this person.", null))
            : read;
    }

    /// <summary>
    /// Ruling 1, the one path every new link takes, checked before anything is written: a known role, a known kind, a target its
    /// <see cref="IDocumentTargets"/> resolves, the right to edit the document and to write the target, no link to it yet. A target the
    /// actor does not read answers as one that does not exist. Returns the link to insert.
    /// </summary>
    public async Task<Result<LinkRecord>> CheckLinkAsync(
        Guid documentId, Guid uploadedBy, IReadOnlyList<LinkRecord> links, DocumentTarget target, string? role, PersonId actor, CancellationToken ct)
    {
        if (role is null || !DocumentRoles.All.Contains(role))
            return Fail(DocumentsCodes.InvalidRole, $"The role '{role}' is not one of {string.Join(", ", DocumentRoles.All)}.", "role");
        if (TargetsFor(target.Kind) is not { } kind)
            return Fail(DocumentsCodes.UnknownTargetKind, $"The target kind '{target.Kind}' is unknown.", "targetKind");
        if (!(await kind.ResolveAsync(target.Kind, [target.Id], ct)).TryGetValue(target.Id, out var resolved))
            return Fail(DocumentsCodes.TargetNotFound, "The target does not exist.", "targetId");

        var link = new LinkFacts(target.Kind, target.Id, resolved.AnchorItemId);
        var facts = await FactsAsync(actor, links.Select(l => l.AnchorItemId).Append(resolved.AnchorItemId).OfType<Guid>().ToList(), ct);
        if (!DocumentRules.CanWriteLink(link, facts))
            return DocumentRules.CanReadLink(link, facts)
                ? Result.Failure<LinkRecord>(new UnauthorizedFailure("The target is read-only for this person.", null))
                : Fail(DocumentsCodes.TargetNotFound, "The target does not exist.", "targetId");
        if (!DocumentRules.CanEdit(uploadedBy, links.Select(Facts), facts))
            return Result.Failure<LinkRecord>(new UnauthorizedFailure("The document is read-only for this person.", null));
        if (links.Any(l => l.TargetKind == target.Kind && l.TargetId == target.Id))
            return Fail(DocumentsCodes.AlreadyLinked, "The document is already linked to this target.", "targetId");

        return Result.Success(new LinkRecord(documentId, target.Kind, target.Id, resolved.AnchorItemId, role, actor.Value, clock.GetCurrentInstant()));
    }

    /// <summary><see cref="CheckLinkAsync"/>, then the insert; a link a concurrent request added first is <c>AlreadyLinked</c> too.</summary>
    public async Task<Result<LinkRecord>> LinkAsync(
        Guid documentId, Guid uploadedBy, IReadOnlyList<LinkRecord> links, DocumentTarget target, string? role, PersonId actor, CancellationToken ct)
    {
        var checkedLink = await CheckLinkAsync(documentId, uploadedBy, links, target, role, actor, ct);
        if (!checkedLink.TryGetValue(out var link))
            return checkedLink;
        return await store.AddLinkAsync(link!, ct)
            ? Result.Success(link!)
            : Fail(DocumentsCodes.AlreadyLinked, "The document is already linked to this target.", "targetId");
    }

    /// <summary>
    /// A role change or an unlink: the link of a document the actor reads, to a target the actor writes. A link the actor does not see
    /// answers as one that does not exist; a link the actor sees but does not write is <c>UnauthorizedFailure</c>.
    /// </summary>
    public async Task<Result<LinkRecord>> WritableLinkAsync(Guid id, DocumentTarget target, PersonId actor, CancellationToken ct)
    {
        var read = await ReadAsync(id, actor, ct);
        if (!read.TryGetValue(out var found))
            return Result.Failure<LinkRecord>(read.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());

        var link = found!.Links.FirstOrDefault(l => l.TargetKind == target.Kind && l.TargetId == target.Id);
        if (link is null || !DocumentRules.CanReadLink(Facts(link), found.Actor))
            return Result.Failure<LinkRecord>(new NotFoundFailure("document link", $"{id}/{target.Kind}/{target.Id}"));
        return DocumentRules.CanWriteLink(Facts(link), found.Actor)
            ? Result.Success(link)
            : Result.Failure<LinkRecord>(new UnauthorizedFailure("The target is read-only for this person.", null));
    }

    /// <summary>The display label of each link's target, by kind and id; a target that no longer resolves is absent.</summary>
    public async Task<IReadOnlyDictionary<(string Kind, Guid Id), string>> LabelsAsync(IEnumerable<LinkRecord> links, CancellationToken ct)
    {
        var labels = new Dictionary<(string, Guid), string>();
        foreach (var group in links.GroupBy(l => l.TargetKind))
            if (TargetsFor(group.Key) is { } kind)
                foreach (var (id, target) in await kind.ResolveAsync(group.Key, group.Select(l => l.TargetId).Distinct().ToList(), ct))
                    labels[(group.Key, id)] = target.Label;
        return labels;
    }

    public bool IsKnownKind(string kind) => TargetsFor(kind) is not null;

    private IDocumentTargets? TargetsFor(string kind) => targets.FirstOrDefault(t => t.Kinds.Contains(kind));

    private static List<Guid> Anchors(IEnumerable<LinkRecord> links) => links.Select(l => l.AnchorItemId).OfType<Guid>().ToList();

    private static Result<ReadableDocument> NotFound(Guid id) => Result.Failure<ReadableDocument>(new NotFoundFailure("document", id.ToString()));

    private static Result<LinkRecord> Fail(string code, string message, string field)
        => Result.Failure<LinkRecord>(new Failure(code, message, new Dictionary<string, object?> { ["field"] = field }));
}
