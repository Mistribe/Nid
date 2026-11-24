using System.Globalization;
using Microsoft.Extensions.Logging;
using Nid.Ai.Contracts;
using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.Documents.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Documents.Application;

public sealed record GetDocumentQuery(Guid Id) : IQuery<DocumentDetail>, IReadsDocument
{
    Guid IReadsDocument.DocumentId => Id;
}

public sealed record GetDocumentContentQuery(Guid Id) : IQuery<DocumentContent>, IReadsDocument
{
    Guid IReadsDocument.DocumentId => Id;
}

/// <summary>
/// Design §7: the documents the actor reads. <c>Household</c> keeps those with a visible link to a shared item, a dependent or the
/// household; <c>Me</c> keeps them all. <c>Inbox</c> keeps those with no visible link, whatever the scope.
/// <c>Target</c> keeps those with a visible link to exactly that target, whatever the scope (design §3.3: every readable document of a
/// row's screen, a private row's too); one the actor cannot see answers an empty list. <c>Anchor</c> keeps those with a visible link
/// anchored on that item (the item, its phases, its one-offs), whatever the scope.
/// </summary>
/// <summary>
/// <paramref name="SourceRefPrefix"/>: syndic documents design §2.1, the documents whose source reference starts with it. Issue #420, on top of those: <paramref name="Role"/> a link role, <paramref name="Year"/> the year of the shown date (the document's
/// own, else its upload day in the household's zone), <paramref name="Linked"/> a visible link's target id, <paramref name="Search"/>
/// part of the title, whatever the case, or (#169) words or a meaning in the documents' text, the most relevant first. <paramref name="Page"/> (from 1) answers <see cref="PageSize"/> documents; null answers them all.
/// </summary>
public sealed record ListDocumentsQuery(VisibilityScope Scope, bool Archived, bool Inbox, DocumentTarget? Target = null, Guid? Anchor = null,
    string? SourceRefPrefix = null, string? Role = null, int? Year = null, Guid? Linked = null, string? Search = null, int? Page = null,
    int PageSize = ListDocumentsQuery.DefaultPageSize) : IQuery<DocumentList>
{
    public const int DefaultPageSize = 50;
}

/// <summary>Tranche 3 design §4.1: the ids among <paramref name="Ids"/> the actor reads; one missing and one unreadable are both left out.</summary>
public sealed record FilterReadableDocumentsQuery(IReadOnlyCollection<Guid> Ids) : IQuery<IReadOnlySet<Guid>>;

/// <summary>Tranche 2 design §2.1: the Core targets the actor may pick from the page; module rows are linked from their own screen.</summary>
public sealed record ListDocumentTargetsQuery() : IQuery<DocumentTargetList>;

/// <summary>Tranche 2 design §2.1: the readers a link would add, after the same checks and refusals as the link; writes nothing.</summary>
public sealed record PreviewDocumentLinkQuery(Guid Id, DocumentTarget Target) : IQuery<LinkPreview>, IOptionalItemScoped, IReadsDocument
{
    Guid IReadsDocument.DocumentId => Id;

    public ItemId? ItemId => Target.Kind == TargetKinds.CoreItem ? new ItemId(Target.Id) : null;

    public AccessKind Access => AccessKind.Write;
}

[RequestHandler<GetDocumentQuery, DocumentDetail>]
public sealed class GetDocumentHandler(DocumentAccess access, IContextAccessor context) : IRequestHandler<GetDocumentQuery, DocumentDetail>
{
    public async ValueTask<Result<DocumentDetail>> HandleAsync(GetDocumentQuery query, CancellationToken ct = default)
    {
        var found = CheckedResources.Of<ReadableDocument>(context, query);
        var d = found.Document;
        return Result.Success(new DocumentDetail(
            DocumentSummary.Of(found, await access.LabelsAsync(found.Links, ct)), d.OriginalFilename, d.Note, d.UploadedBy));
    }
}

[RequestHandler<ListDocumentsQuery, DocumentList>]
public sealed class ListDocumentsHandler(
    IDocumentStore store, DocumentAccess access, IVisibleItems visible, IHouseholdCalendar calendar, IActorAccessor actor, IEmbeddings embeddings)
    : IRequestHandler<ListDocumentsQuery, DocumentList>
{
    /// <summary>#169: how long the search waits for its text's vector before it answers by the words alone.</summary>
    public static readonly TimeSpan MeaningWait = TimeSpan.FromSeconds(2);

    public async ValueTask<Result<DocumentList>> HandleAsync(ListDocumentsQuery query, CancellationToken ct = default)
    {
        var person = actor.Current!.PersonId!.Value;
        if (query.Target is { } asked && !access.IsKnownKind(asked.Kind))
            return Result.Failure<DocumentList>(new Failure(DocumentsCodes.UnknownTargetKind, $"The target kind '{asked.Kind}' is unknown.",
                new Dictionary<string, object?> { ["field"] = "targetKind" }));
        // One rights check over every anchored item, then the list's question in SQL: a constant number of queries.
        var facts = await access.FactsAsync(person, await store.AnchorItemsAsync(ct), ct, await store.UnanchoredTargetsOfAsync(person.Value, ct));
        var shared = query is { Scope: VisibilityScope.Household, Inbox: false, Target: null, Anchor: null }
            ? (await visible.ForAsync(person, VisibilityScope.Household, includeArchived: true, ct)).Select(i => i.Value).ToList()
            : null;
        var size = Math.Max(query.PageSize, 1);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var (meaning, meaningState) = search is null ? (null, null) : await MeaningAsync(search, person.Value, ct);
        var found = await store.PageAsync(new DocumentPageQuery(
            person.Value, facts.ReadableItems, [facts.Person, .. facts.Dependents], facts.Dependents, query.Archived,
            // Read as a reference is stored: trimmed, blank meaning no filter.
            DocumentFields.SourceRef(query.SourceRefPrefix), query.Inbox, query.Target, query.Anchor, shared,
            string.IsNullOrEmpty(query.Role) ? null : query.Role, query.Year, query.Linked,
            search, await calendar.ZoneAsync(ct),
            query.Page is { } page ? (Math.Max(page, 1) - 1) * size : 0, query.Page is null ? null : size,
            facts.WritableUnanchored, meaning), ct);

        // The facets' targets hold every readable link of the page: one label lookup serves both.
        var labels = await access.LabelsAsync(found.Links.Select(l => (l.TargetKind, l.TargetId)), ct);
        var links = (await store.LinksOfAsync(found.Items.Select(d => d.Id).ToList(), ct)).ToLookup(l => l.DocumentId);
        var facets = new DocumentFacets(found.Roles, found.Years, found.Links
            .Select(l => new DocumentLinkOption(l.TargetKind, l.TargetId, l.AnchorItemId, labels.GetValueOrDefault((l.TargetKind, l.TargetId), DocumentSummary.DeletedTargetLabel)))
            .ToList());
        return Result.Success(new DocumentList(
            found.Items.Select(d => DocumentSummary.Of(new ReadableDocument(d, links[d.Id].ToList(), facts), labels, found.Matches?.GetValueOrDefault(d.Id)))
                .ToList(), found.Total, facets, meaningState));
    }

    /// <summary>
    /// #169: the search text's vector, through the gateway as the indexer's (#559): the reader is the one concerned, so their own
    /// preference decides whether a remote worker may see it. No model, a refusal or no answer within <see cref="MeaningWait"/>:
    /// the search answers by the words alone, and says so.
    /// </summary>
    private async Task<(SearchMeaning?, string)> MeaningAsync(string search, Guid reader, CancellationToken ct)
    {
        if (await embeddings.ModelAsync(ct) is not { } model)
            return (null, MeaningSearchStates.Off);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(MeaningWait);
        try
        {
            var embedded = await embeddings.EmbedAsync(new LlmEmbedRequest([search], DataClasses.Documents, [reader]), wait.Token);
            return embedded.TryGetValue(out var result) && result.Model == model && result.Vectors is [var vector]
                ? (new SearchMeaning(model, result.Dimension, vector), MeaningSearchStates.Used)
                : (null, MeaningSearchStates.Unavailable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, MeaningSearchStates.Unavailable);
        }
    }
}

[RequestHandler<FilterReadableDocumentsQuery, IReadOnlySet<Guid>>]
public sealed class FilterReadableDocumentsHandler(IDocumentStore store, DocumentAccess access, IActorAccessor actor)
    : IRequestHandler<FilterReadableDocumentsQuery, IReadOnlySet<Guid>>
{
    public async ValueTask<Result<IReadOnlySet<Guid>>> HandleAsync(FilterReadableDocumentsQuery query, CancellationToken ct = default)
    {
        if (query.Ids.Count == 0)
            return Result.Success<IReadOnlySet<Guid>>(new HashSet<Guid>());

        // As the list: the documents, their links and one rights check for every anchor, whatever the number of ids.
        var wanted = query.Ids.ToHashSet();
        var documents = (await store.ListAsync(includeArchived: true, ct)).Where(d => wanted.Contains(d.Id)).ToList();
        var links = (await store.LinksOfAsync(documents.Select(d => d.Id).ToList(), ct)).ToLookup(l => l.DocumentId);
        var facts = await access.FactsAsync(actor.Current!.PersonId!.Value, links.SelectMany(l => l).Select(l => l.AnchorItemId).OfType<Guid>().ToList(), ct);
        return Result.Success<IReadOnlySet<Guid>>(documents
            .Where(d => DocumentRules.CanRead(d.UploadedBy, links[d.Id].Select(DocumentAccess.Facts), facts)).Select(d => d.Id).ToHashSet());
    }
}

/// <summary>Design §5: the only way to the bytes, after the same check as the detail.</summary>
[RequestHandler<GetDocumentContentQuery, DocumentContent>]
public sealed partial class GetDocumentContentHandler(
    IContextAccessor context, IDocumentStorage storage, ILogger<GetDocumentContentHandler> logger)
    : IRequestHandler<GetDocumentContentQuery, DocumentContent>
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Document {DocumentId}: its stored file {Sha256} is missing.")]
    private partial void LogFileMissing(Guid documentId, string sha256);

    public async ValueTask<Result<DocumentContent>> HandleAsync(GetDocumentContentQuery query, CancellationToken ct = default)
    {
        var d = CheckedResources.Of<ReadableDocument>(context, query).Document;
        if (await storage.OpenAsync(d.Sha256, ct) is not { } content)
        {
            // Review Focus 4: a database restored without its volume.
            LogFileMissing(d.Id, d.Sha256);
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
public sealed class PreviewDocumentLinkHandler(DocumentAccess access, IPersonRegistry persons, IActorAccessor actor, IContextAccessor context)
    : IRequestHandler<PreviewDocumentLinkQuery, LinkPreview>
{
    public async ValueTask<Result<LinkPreview>> HandleAsync(PreviewDocumentLinkQuery query, CancellationToken ct = default)
    {
        var person = actor.Current!.PersonId!.Value;
        var found = CheckedResources.Of<ReadableDocument>(context, query);

        // The link's own checks, role aside: the preview names none, and any known role passes them alike.
        var d = found.Document;
        var checkedLink = await access.CheckLinkAsync(d.Id, d.UploadedBy, found.Links, query.Target, DocumentRoles.Other, person, ct);
        if (!checkedLink.TryGetValue(out var link))
            return Result.Failure<LinkPreview>(checkedLink.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());

        var before = await access.ReadersAsync(d.UploadedBy, found.Links.Select(DocumentAccess.Facts).ToList(), ct);
        var after = await access.ReadersAsync(d.UploadedBy, found.Links.Append(link!).Select(DocumentAccess.Facts).ToList(), ct);
        var names = (await persons.ListAsync(ct)).ToDictionary(p => p.Id, p => p.DisplayName);
        return Result.Success(new LinkPreview(after.Except(before).Select(p => new ReaderView(p.Value, names.GetValueOrDefault(p, ""))).ToList()));
    }
}
