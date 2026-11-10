using Nid.Documents.Contracts;

namespace Nid.Income.Application;

/// <summary>#23: drops the document dismissals anchored on phases and one-off incomes; runs in the caller's unit of work.</summary>
public sealed class IncomeDocumentUnlinks(IDocumentLinks documents)
{
    public async Task UnlinkPhasesAsync(IEnumerable<Guid> phaseIds, CancellationToken ct)
    {
        foreach (var id in phaseIds)
            await documents.UnlinkTargetAsync(new DocumentTarget(IncomeDocumentTargets.PhaseKind, id), ct);
    }

    public async Task UnlinkOneOffsAsync(IEnumerable<Guid> flowIds, CancellationToken ct)
    {
        foreach (var id in flowIds)
            await documents.UnlinkTargetAsync(new DocumentTarget(IncomeDocumentTargets.OneOffKind, id), ct);
    }
}
