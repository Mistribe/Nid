using Nid.Core.Contracts;

namespace Nid.Documents.Contracts;

public static class TargetKinds
{
    public const string CoreItem = "core.item";
    public const string CorePerson = "core.person";
    public const string CoreHousehold = "core.household";

    /// <summary>The household has no id of its own: its target id is always empty.</summary>
    public static readonly Guid Household = Guid.Empty;
}

public sealed record DocumentTarget(string Kind, Guid Id);

public sealed record ResolvedTarget(Guid? AnchorItemId, string Label);

/// <summary>#84: a document linked to a target, as its reader sees it in the target's own screen.</summary>
public sealed record LinkedDocumentView(Guid Id, string Title);

public interface IDocumentTargets
{
    IReadOnlySet<string> Kinds { get; }

    /// <summary>An id missing from the answer does not exist.</summary>
    Task<IReadOnlyDictionary<Guid, ResolvedTarget>> ResolveAsync(string kind, IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>
    /// #84: those of <paramref name="ids"/>, targets <see cref="ResolveAsync"/> gives no anchor, that <paramref name="actor"/> writes. The document rules
    /// know no reader of such a target but a person's and the household's: a link to one is its document's uploader's alone, while they write it.
    /// </summary>
    Task<IReadOnlySet<Guid>> WritableUnanchoredAsync(string kind, IReadOnlyCollection<Guid> ids, PersonId actor, CancellationToken ct)
        => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
}

public interface IDocumentLinks
{
    Task UnlinkTargetAsync(DocumentTarget target, CancellationToken ct);

    /// <summary>
    /// Rewrites every link to <paramref name="from"/> so it points at <paramref name="to"/>, keeping role, anchor, author and date.
    /// The caller guarantees <paramref name="to"/> has the same anchor as <paramref name="from"/>: the stored anchor is kept, not recomputed.
    /// A document already linked to <paramref name="to"/> keeps that link; its link to <paramref name="from"/> is dropped.
    /// </summary>
    Task MoveTargetAsync(DocumentTarget from, DocumentTarget to, CancellationToken ct);

    /// <summary>#84: sets the anchor of every link to these targets (null: none), in the caller's transaction, when what they count through changes.</summary>
    Task ReanchorTargetsAsync(string kind, IReadOnlyCollection<Guid> targetIds, Guid? anchor, CancellationToken ct);

    /// <summary>#84: those of <paramref name="targetIds"/> that some document is linked to.</summary>
    Task<IReadOnlySet<Guid>> TargetIdsWithLinksAsync(string kind, IReadOnlyCollection<Guid> targetIds, CancellationToken ct);

    /// <summary>#84: by target, the documents linked to it that <paramref name="reader"/> reads, by title; a target with none is absent.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<LinkedDocumentView>>> ReadableOfTargetsAsync(
        string kind, IReadOnlyCollection<Guid> targetIds, PersonId reader, CancellationToken ct);
}
