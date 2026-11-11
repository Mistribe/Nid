namespace Nid.Core.Contracts;

/// <summary>
/// #156: one module's rows that still name a person, by stable key (<c>ownerships</c>, <c>childcare</c>…). A dependent is
/// removed only when every part answers nothing: the check blocks, it never cleans up. Counted whatever the reader may see:
/// the refusal names the keys only.
/// </summary>
public interface IPersonUsePart
{
    Task<IReadOnlyList<DeletionCount>> UsesAsync(PersonId person, CancellationToken ct);
}
