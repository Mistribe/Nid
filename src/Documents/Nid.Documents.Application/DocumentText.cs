using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nid.Ai.Contracts;
using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using NodaTime;

namespace Nid.Documents.Application;

/// <summary>One page of a document's text (#167), numbered from 1, with where it comes from: <c>text-layer</c>, <c>ocr</c>,
/// <c>email</c> or <c>none</c>.</summary>
public sealed record DocumentTextPage(int Number, string Text, string Source);

/// <summary>The <c>documents.document_text</c> pages and the <c>document_text_job</c> that asked for them.</summary>
public interface IDocumentTextStore
{
    /// <summary>Records that the document's text was asked for (once: the backfill skips it).</summary>
    Task QueuedAsync(Guid document, Guid job, Instant at, CancellationToken ct);

    /// <summary>Documents whose text was never asked for, oldest first.</summary>
    Task<IReadOnlyList<DocumentRecord>> NeverQueuedAsync(int limit, CancellationToken ct);

    /// <summary>Replaces the document's pages; nothing when the document is gone.</summary>
    Task SaveAsync(Guid document, IReadOnlyList<DocumentTextPage> pages, Instant at, CancellationToken ct);

    Task<IReadOnlyList<DocumentTextPage>> PagesAsync(Guid document, CancellationToken ct);
}

/// <summary>Asks a worker for a document's text (<c>document-text</c>, data class <see cref="DataClassOf"/>). It leaves the premises
/// only with <c>allow</c> from each adult concerned (decision M, <c>docs/ai/README.md</c> §6.3): its uploader and whoever owns
/// what it is linked to, read when it is queued.</summary>
public sealed partial class DocumentTexts(
    IModelJobs jobs, IDocumentTextStore texts, IDocumentStore documents, IItemRegistry items, IPersonRegistry persons, IClock clock,
    ILogger<DocumentTexts> logger)
{
    public const string JobType = "document-text";
    public const string FileModule = "documents";

    /// <summary><see cref="QueueAsync"/> for an upload, which never fails for it: a failure is logged.</summary>
    public async Task TryQueueAsync(DocumentRecord document, IReadOnlyList<LinkRecord> links, CancellationToken ct)
    {
        try
        {
            await QueueAsync(document, links, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            NotQueued(logger, e, document.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The text of document {Document} was not queued; the next start's backfill asks again")]
    private static partial void NotQueued(ILogger logger, Exception error, Guid document);

    /// <summary>False when the job was refused (the reason is Ai's); the backfill of the next start asks again.</summary>
    public async Task<bool> QueueAsync(DocumentRecord document, IReadOnlyList<LinkRecord> links, CancellationToken ct)
    {
        var submitted = await jobs.SubmitAsync(new JobRequest(
            JobType, 1, JsonSerializer.SerializeToElement(new { mediaType = document.MediaType }), DataClassOf(links),
            FileRef(document.Id), Concerned: await ConcernedAsync(document.UploadedBy, links, ct)), ct);
        if (!submitted.TryGetValue(out var job))
            return false;
        await texts.QueuedAsync(document.Id, job, clock.GetCurrentInstant(), ct);
        return true;
    }

    /// <summary>#599: once the document's links changed, its text job, while no worker has it yet, takes the class and the adults
    /// they give now; one already sent or finished is left as it is.</summary>
    public async Task ReclassAsync(Guid document, CancellationToken ct)
    {
        if (await documents.FindAsync(document, ct) is not { } found)
            return;
        var links = await documents.LinksOfAsync([document], ct);
        await jobs.ReclassAsync(FileRef(document), DataClassOf(links), await ConcernedAsync(found.UploadedBy, links, ct), ct);
    }

    /// <summary>
    /// What a model may see of a document (#598, <c>llm-providers.md</c> §2.7): a payslip stays on the premises, so only a <c>safe</c>
    /// worker reads its file or embeds its text; so does a document « À classer » (no link, #599), which may be one until it is filed.
    /// Any other document is the household's.
    /// </summary>
    public static string DataClassOf(IReadOnlyList<LinkRecord> links)
        => links.Count == 0 || links.Any(l => l.Role == DocumentRoles.Payslip) ? DataClasses.LocalOnly : DataClasses.Documents;

    internal static string FileRef(Guid document) => $"{FileModule}:{document}";

    /// <summary>The adults a document concerns (decision M): its uploader, whoever owns what it is linked to, a linked person, both adults for the household.</summary>
    public async Task<IReadOnlyList<Guid>> ConcernedAsync(Guid uploadedBy, IReadOnlyList<LinkRecord> links, CancellationToken ct)
    {
        var adults = (await persons.AdultIdsAsync(ct)).Select(p => p.Value).ToHashSet();
        var concerned = new HashSet<Guid> { uploadedBy };
        if (links.Any(l => l.TargetKind == TargetKinds.CoreHousehold))
            concerned.UnionWith(adults);
        concerned.UnionWith(links.Where(l => l.TargetKind == TargetKinds.CorePerson).Select(l => l.TargetId));
        var anchors = links.Select(l => l.AnchorItemId).OfType<Guid>().Distinct().Select(a => new ItemId(a)).ToList();
        if (anchors.Count > 0)
            foreach (var item in (await items.FindManyAsync(anchors, ct)).Values)
                concerned.UnionWith(item.Owners.Select(o => o.PersonId.Value));
        return [.. concerned.Where(adults.Contains).Order()];
    }
}

/// <summary>#167: asks for the text of every document stored before it, once (a document with a job is skipped), in batches.</summary>
public sealed class DocumentTextBackfill(IDocumentTextStore texts, IDocumentStore documents, DocumentTexts queue)
{
    private const int Batch = 100;

    /// <summary>The number queued. A refused one stops the run: the next start tries again rather than looping on it.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var queued = 0;
        while (await texts.NeverQueuedAsync(Batch, ct) is { Count: > 0 } batch)
        {
            var links = (await documents.LinksOfAsync([.. batch.Select(d => d.Id)], ct)).ToLookup(l => l.DocumentId);
            foreach (var document in batch)
            {
                if (!await queue.QueueAsync(document, [.. links[document.Id]], ct))
                    return queued;
                queued++;
            }
        }

        return queued;
    }
}

/// <summary>Stores a <c>document-text</c> job's pages. The result was read through the job's contract by Ai already.</summary>
public sealed class DocumentTextSink(IDocumentTextStore texts, IClock clock) : IJobResultSink
{
    public string Type => DocumentTexts.JobType;

    public async Task AcceptAsync(string? fileRef, string result, CancellationToken ct)
    {
        if (DocumentFileSource.IdOf(fileRef?.Split(':', 2) is [DocumentTexts.FileModule, var reference] ? reference : null) is not { } id)
            return;
        var output = JsonSerializer.Deserialize<Output>(result, JsonSerializerOptions.Web)!;
        await texts.SaveAsync(id, [.. output.Pages.Select(p => new DocumentTextPage(p.Number, p.Text, p.Source))], clock.GetCurrentInstant(), ct);
    }

    private sealed record Output(IReadOnlyList<DocumentTextPage> Pages);
}

/// <summary>A document's bytes for its job: the reference is the document's id.</summary>
public sealed class DocumentFileSource(IDocumentStore store, IDocumentStorage storage) : IJobFileSource
{
    public string Module => DocumentTexts.FileModule;

    public async Task<byte[]?> ReadAsync(string reference, CancellationToken ct)
    {
        if (IdOf(reference) is not { } id || await store.FindAsync(id, ct) is not { } document)
            return null;
        await using var stream = await storage.OpenAsync(document.Sha256, ct);
        if (stream is null)
            return null;
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes, ct);
        return bytes.ToArray();
    }

    internal static Guid? IdOf(string? reference) => Guid.TryParse(reference, out var id) ? id : null;
}
