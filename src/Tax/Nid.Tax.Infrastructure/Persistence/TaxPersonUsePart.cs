using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;

namespace Nid.Tax.Infrastructure.Persistence;

/// <summary>#156: the tax notices a person declares.</summary>
public sealed class TaxPersonUsePart(TaxDbContext db) : IPersonUsePart
{
    public async Task<IReadOnlyList<DeletionCount>> UsesAsync(PersonId person, CancellationToken ct)
        => [new("tax_assessments", await db.TaxAssessmentDeclarants.CountAsync(d => d.PersonId == person.Value, ct))];
}
