using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.Investments.Contracts;

namespace Nid.Investments.Application;

/// <summary>Issue #414: an open life insurance expects its insurer's yearly statement, from the year it was opened.</summary>
public sealed class EnvelopeExpectedDocuments(IEnvelopeStore envelopes) : IExpectedDocumentSubjects
{
    public async Task<IReadOnlyDictionary<ItemId, ExpectedSubject>> SubjectsAsync(IReadOnlyCollection<ItemAccess> items, CancellationToken ct)
        => (await envelopes.FindManyAsync(items.Where(i => i.Module == "investments").Select(i => i.Id).ToList(), ct))
            .Where(e => e is { Kind: EnvelopeKind.LifeInsurance, ClosedOn: null })
            .ToDictionary(e => e.ItemId, e => new ExpectedSubject(ExpectedSubjects.LifeInsurance, e.OpenedOn));
}
