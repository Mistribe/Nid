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

public interface IDocumentTargets
{
    IReadOnlySet<string> Kinds { get; }

    /// <summary>An id missing from the answer does not exist.</summary>
    Task<IReadOnlyDictionary<Guid, ResolvedTarget>> ResolveAsync(string kind, IReadOnlyCollection<Guid> ids, CancellationToken ct);
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
}
