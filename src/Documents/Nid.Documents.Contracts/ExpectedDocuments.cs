using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Documents.Contracts;

/// <summary>Issue #414: the subjects a module's item can be in the expected documents; each names a rule of <c>ExpectedDocumentRules</c>.</summary>
public static class ExpectedSubjects
{
    public const string LifeInsurance = "life_insurance";
    public const string Loan = "loan";
    public const string Property = "property";
}

/// <summary>An item's subject (<see cref="ExpectedSubjects"/>), and its start: no yearly document is asked for a year due before it.</summary>
public sealed record ExpectedSubject(string Kind, LocalDate? Since);

/// <summary>
/// #511: the expected documents' extension point. Each module says which of its own items expect documents: Documents reads none
/// of their tables.
/// </summary>
public interface IExpectedDocumentSubjects
{
    /// <summary>Of <paramref name="items"/> (active, of any module), those of this module that expect documents; the others are absent.</summary>
    Task<IReadOnlyDictionary<ItemId, ExpectedSubject>> SubjectsAsync(IReadOnlyCollection<ItemAccess> items, CancellationToken ct);
}
