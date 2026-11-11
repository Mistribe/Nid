using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Documents.Contracts;

namespace Nid.Documents.Infrastructure.Persistence;

/// <summary>#156: the documents filed under a person (a child's health booklet): removing the child would hide them from the household.</summary>
public sealed class DocumentsPersonUsePart(DocumentsDbContext db) : IPersonUsePart
{
    public async Task<IReadOnlyList<DeletionCount>> UsesAsync(PersonId person, CancellationToken ct)
        => [new("documents", await db.Links.CountAsync(l => l.TargetKind == TargetKinds.CorePerson && l.TargetId == person.Value, ct))];
}
