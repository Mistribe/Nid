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
/// </summary>
public sealed record ListDocumentsQuery(VisibilityScope Scope, bool Archived, bool Inbox) : IQuery<DocumentList>;

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
        var documents = await store.ListAsync(query.Archived, ct);
        var links = (await store.LinksOfAsync(documents.Select(d => d.Id).ToList(), ct)).ToLookup(l => l.DocumentId);
        // One rights check for every anchor of every document: a constant number of queries.
        var facts = await access.FactsAsync(person, links.SelectMany(l => l).Select(l => l.AnchorItemId).OfType<Guid>().ToList(), ct);
        var household = query is { Scope: VisibilityScope.Household, Inbox: false }
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
