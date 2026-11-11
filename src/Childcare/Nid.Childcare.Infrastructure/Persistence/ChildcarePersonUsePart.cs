using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;

namespace Nid.Childcare.Infrastructure.Persistence;

/// <summary>#156: the childcare contracts kept for a child.</summary>
public sealed class ChildcarePersonUsePart(ChildcareDbContext db) : IPersonUsePart
{
    public async Task<IReadOnlyList<DeletionCount>> UsesAsync(PersonId person, CancellationToken ct)
        => [new("childcare", await db.Arrangements.CountAsync(a => a.ChildId == person.Value, ct))];
}
