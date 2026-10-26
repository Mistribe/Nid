using System.Globalization;
using Microsoft.Extensions.Logging;
using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.Documents.Domain;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Documents.Application;

public sealed record GetDocumentQuery(Guid Id) : IQuery<DocumentDetail>;

public sealed record GetDocumentContentQuery(Guid Id) : IQuery<DocumentContent>;

/// <summary>
/// Design §7: the documents the actor reads. <c>Household</c> keeps those with a visible link to a shared item, a dependent or the
/// household; <c>Me</c> keeps them all. <c>Inbox</c> keeps those with no visible link, whatever the scope.
/// <c>Target</c> keeps those with a visible link to exactly that target, whatever the scope (design §3.3: every readable document of a
/// row's screen, a private row's too); one the actor cannot see answers an empty list.
/// </summary>
public sealed record ListDocumentsQuery(VisibilityScope Scope, bool Archived, bool Inbox, DocumentTarget? Target = null) : IQuery<DocumentList>;

/// <summary>Tranche 2 design §2.1: the Core targets the actor may pick from the page; module rows are linked from their own screen.</summary>
public sealed record ListDocumentTargetsQuery() : IQuery<DocumentTargetList>;

/// <summary>Tranche 2 design §2.1: the readers a link would add, after the same checks and refusals as the link; writes nothing.</summary>
public sealed record PreviewDocumentLinkQuery(Guid Id, DocumentTarget Target) : IQuery<LinkPreview>, IOptionalItemScoped
{
    public ItemId? ItemId => Target.Kind == TargetKinds.CoreItem ? new ItemId(Target.Id) : null;

    public AccessKind Access => AccessKind.Write;
}

[RequestHandler<GetDocumentQuery, DocumentDetail>]
public sealed class GetDocumentHandler(DocumentAccess access, IActorAccessor actor) : IRequestHandler<GetDocumentQuery, DocumentDetail>
{
    public async ValueTask<Result<DocumentDetail>> HandleAsync(GetDocumentQuery query, CancellationToken ct = default)
    {
        var read = await access.ReadAsync(query.Id, actor.Current!.PersonId!.Value, ct);
        if (!read.TryGetValue(out var found))
            return Result.Failure<DocumentDetail>(read.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());

        var d = found!.Document;
        return Result.Success(new DocumentDetail(
            DocumentSummary.Of(found, await access.LabelsAsync(found.Links, ct)), d.OriginalFilename, d.Note, d.UploadedBy));
    }
}

[RequestHandler<ListDocumentsQuery, DocumentList>]
public sealed class ListDocumentsHandler(IDocumentStore store, DocumentAccess access, IVisibleItems visible, IActorAccessor actor)
    : IRequestHandler<ListDocumentsQuery, DocumentList>
{
    public async ValueTask<Result<DocumentList>> HandleAsync(ListDocumentsQuery query, CancellationToken ct = default)
    {
        var person = actor.Current!.PersonId!.Value;
        if (query.Target is { } asked && !access.IsKnownKind(asked.Kind))
            return Result.Failure<DocumentList>(new Failure(DocumentsCodes.UnknownTargetKind, $"The target kind '{asked.Kind}' is unknown.",
                new Dictionary<string, object?> { ["field"] = "targetKind" }));
        var documents = await store.ListAsync(query.Archived, ct);
        if (query.Target is { } target)
        {
            var linked = await store.DocumentsLinkedToAsync(target, ct);
            documents = documents.Where(d => linked.Contains(d.Id)).ToList();
        }

        var links = (await store.LinksOfAsync(documents.Select(d => d.Id).ToList(), ct)).ToLookup(l => l.DocumentId);
        // One rights check for every anchor of every document: a constant number of queries.
        var facts = await access.FactsAsync(person, links.SelectMany(l => l).Select(l => l.AnchorItemId).OfType<Guid>().ToList(), ct);
        var household = query is { Scope: VisibilityScope.Household, Inbox: false, Target: null }
            ? await visible.ForAsync(person, VisibilityScope.Household, includeArchived: true, ct)
            : null;

        bool Shared(LinkRecord l) => l switch
        {
            { AnchorItemId: { } anchor } => household!.Contains(new ItemId(anchor)),
            { TargetKind: TargetKinds.CorePerson } => facts.Dependents.Contains(l.TargetId),
            { TargetKind: TargetKinds.CoreHousehold } => true,
            _ => false,
        };

        var kept = new List<ReadableDocument>();
        foreach (var d in documents.OrderByDescending(d => d.DocumentDate).ThenByDescending(d => d.UploadedAt)) // a null date sorts last
        {
            var all = links[d.Id].ToList();
            if (!DocumentRules.CanRead(d.UploadedBy, all.Select(DocumentAccess.Facts), facts))
                continue;
            var seen = all.Where(l => DocumentRules.CanReadLink(DocumentAccess.Facts(l), facts)).ToList();
            if (query.Target is { } t && !seen.Any(l => l.TargetKind == t.Kind && l.TargetId == t.Id))
                continue;
            if (query.Inbox ? seen.Count > 0 : household is not null && !seen.Any(Shared))
                continue;
            kept.Add(new ReadableDocument(d, all, facts));
        }

        var labels = await access.LabelsAsync(kept.SelectMany(r => r.Links).Where(l => DocumentRules.CanReadLink(DocumentAccess.Facts(l), facts)), ct);
        return Result.Success(new DocumentList(kept.Select(r => DocumentSummary.Of(r, labels)).ToList()));
    }
}

/// <summary>Design §5: the only way to the bytes, after the same check as the detail.</summary>
[RequestHandler<GetDocumentContentQuery, DocumentContent>]
public sealed class GetDocumentContentHandler(
    DocumentAccess access, IDocumentStorage storage, IActorAccessor actor, ILogger<GetDocumentContentHandler> logger)
    : IRequestHandler<GetDocumentContentQuery, DocumentContent>
{
    public async ValueTask<Result<DocumentContent>> HandleAsync(GetDocumentContentQuery query, CancellationToken ct = default)
    {
        var read = await access.ReadAsync(query.Id, actor.Current!.PersonId!.Value, ct);
        if (!read.TryGetValue(out var found))
            return Result.Failure<DocumentContent>(read.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());

        var d = found!.Document;
        if (await storage.OpenAsync(d.Sha256, ct) is not { } content)
        {
            // Review Focus 4: a database restored without its volume.
            logger.LogWarning("Document {DocumentId}: its stored file {Sha256} is missing.", d.Id, d.Sha256);
            return Result.Failure<DocumentContent>(new Failure(DocumentsCodes.ContentMissing, "The document's file is missing from the storage.", null));
        }

        return Result.Success(new DocumentContent(content, d.MediaType, d.OriginalFilename));
    }
}

[RequestHandler<ListDocumentTargetsQuery, DocumentTargetList>]
public sealed class ListDocumentTargetsHandler(
    DocumentAccess access, IVisibleItems visible, IItemRegistry items, IPersonRegistry persons, IActorAccessor actor)
    : IRequestHandler<ListDocumentTargetsQuery, DocumentTargetList>
{
    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    public async ValueTask<Result<DocumentTargetList>> HandleAsync(ListDocumentTargetsQuery query, CancellationToken ct = default)
    {
        var person = actor.Current!.PersonId!.Value;
        // Active items in « Moi »: the shared ones and the actor's own private ones, never the other adult's.
        var found = await items.FindManyAsync((await visible.ForAsync(person, VisibilityScope.Me, ct)).ToList(), ct);
        var facts = await access.FactsAsync(person, found.Keys.Select(i => i.Value).ToList(), ct);
        bool CanWrite(string kind, Guid id, Guid? anchor) => DocumentRules.CanWriteLink(new LinkFacts(kind, id, anchor), facts);

        var targets = found.Values.OrderBy(i => i.DisplayName, French).ThenBy(i => i.Id.Value)
            .Select(i => new DocumentTargetView(TargetKinds.CoreItem, i.Id.Value, i.DisplayName, i.Module, i.Kind,
                CanWrite(TargetKinds.CoreItem, i.Id.Value, i.Id.Value)))
            .Concat((await persons.ListAsync(ct)).Where(p => !p.IsPending)
                .Select(p => new DocumentTargetView(TargetKinds.CorePerson, p.Id.Value, p.DisplayName, null, null,
                    CanWrite(TargetKinds.CorePerson, p.Id.Value, null))))
            .Append(new DocumentTargetView(TargetKinds.CoreHousehold, TargetKinds.Household, CoreDocumentTargets.HouseholdLabel, null, null,
                CanWrite(TargetKinds.CoreHousehold, TargetKinds.Household, null)))
            .ToList();
        return Result.Success(new DocumentTargetList(targets));
    }
}

[RequestHandler<PreviewDocumentLinkQuery, LinkPreview>]
public sealed class PreviewDocumentLinkHandler(DocumentAccess access, IPersonRegistry persons, IActorAccessor actor)
    : IRequestHandler<PreviewDocumentLinkQuery, LinkPreview>
{
    public async ValueTask<Result<LinkPreview>> HandleAsync(PreviewDocumentLinkQuery query, CancellationToken ct = default)
    {
        var person = actor.Current!.PersonId!.Value;
        var read = await access.ReadAsync(query.Id, person, ct);
        if (!read.TryGetValue(out var found))
            return Result.Failure<LinkPreview>(read.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());

        // The link's own checks, role aside: the preview names none, and any known role passes them alike.
        var d = found!.Document;
        var checkedLink = await access.CheckLinkAsync(d.Id, d.UploadedBy, found.Links, query.Target, DocumentRoles.Other, person, ct);
        if (!checkedLink.TryGetValue(out var link))
            return Result.Failure<LinkPreview>(checkedLink.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());

        var before = await access.ReadersAsync(d.UploadedBy, found.Links.Select(DocumentAccess.Facts).ToList(), ct);
        var after = await access.ReadersAsync(d.UploadedBy, found.Links.Append(link!).Select(DocumentAccess.Facts).ToList(), ct);
        var names = (await persons.ListAsync(ct)).ToDictionary(p => p.Id, p => p.DisplayName);
        return Result.Success(new LinkPreview(after.Except(before).Select(p => new ReaderView(p.Value, names.GetValueOrDefault(p, ""))).ToList()));
    }
}
