using Nid.Core.Contracts;
using Nid.Investments.Contracts;

namespace Nid.Investments.Application;

/// <summary>Breakdowns spec C10: an approval's source document goes onto the breakdown rows it wrote.</summary>
public sealed class BreakdownDocumentStamp(ISecurityBreakdownStore breakdowns) : IAppliedDocumentStamp
{
    public Task StampAsync(Guid documentId, IReadOnlyList<Guid> appliedRefs, CancellationToken ct)
        => breakdowns.StampDocumentAsync(documentId, appliedRefs, ct);
}
