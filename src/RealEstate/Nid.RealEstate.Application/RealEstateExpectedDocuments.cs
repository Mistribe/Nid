using Nid.Core.Contracts;
using Nid.Documents.Contracts;

namespace Nid.RealEstate.Application;

/// <summary>Issue #414: a loan expects its amortization table, a property its deed, once each.</summary>
public sealed class RealEstateExpectedDocuments : IExpectedDocumentSubjects
{
    public Task<IReadOnlyDictionary<ItemId, ExpectedSubject>> SubjectsAsync(IReadOnlyCollection<ItemAccess> items, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<ItemId, ExpectedSubject>>(items
            .Where(i => i is { Module: "realestate", Kind: "loan" or "property" })
            .ToDictionary(i => i.Id, i => new ExpectedSubject(i.Kind == "loan" ? ExpectedSubjects.Loan : ExpectedSubjects.Property, null)));
}
