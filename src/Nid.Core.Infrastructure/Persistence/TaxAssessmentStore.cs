using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Npgsql;

namespace Nid.Core.Infrastructure.Persistence;

/// <summary>
/// Tax assessments spec §3: a notice as a camelCase JSON document, its declarants as rows beside it (the unique (year, person) index
/// is what keeps a person to one notice a year, even under two concurrent writes).
/// </summary>
public sealed partial class TaxAssessmentStore(CoreDbContext db) : ITaxAssessmentStore, ITaxAssessmentDeclarants
{
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
    [JsonSerializable(typeof(TaxAssessment))]
    private sealed partial class TaxAssessmentJson : JsonSerializerContext;

    public async Task<IReadOnlyList<StoredTaxAssessment>> ListForAsync(PersonId reader, int? year, CancellationToken ct)
    {
        var mine = db.TaxAssessmentDeclarants.Where(d => d.PersonId == reader.Value).Select(d => d.AssessmentId);
        var rows = await db.TaxAssessments.AsNoTracking().Where(a => mine.Contains(a.Id) && (year == null || a.Year == year)).ToListAsync(ct);
        return rows.Select(ToStored).ToList();
    }

    public async Task<StoredTaxAssessment?> FindAsync(Guid id, CancellationToken ct)
        => await db.TaxAssessments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) is { } row ? ToStored(row) : null;

    public Task<bool> PersonTakenAsync(int year, IReadOnlyCollection<PersonId> persons, Guid? except, CancellationToken ct)
    {
        var ids = persons.Select(p => p.Value).ToList();
        return db.TaxAssessmentDeclarants.AnyAsync(d => d.Year == year && ids.Contains(d.PersonId) && d.AssessmentId != except, ct);
    }

    public async Task<Guid> AddAsync(TaxAssessment a, CancellationToken ct)
    {
        var row = new TaxAssessmentRow { Id = Guid.CreateVersion7(), Year = a.Year, Data = Serialize(a) };
        db.TaxAssessments.Add(row);
        db.TaxAssessmentDeclarants.AddRange(a.Declarants.Select(d => Declarant(row.Id, a.Year, d)));
        await SaveAsync(ct);
        return row.Id;
    }

    public async Task UpdateAsync(Guid id, TaxAssessment a, uint version, CancellationToken ct)
    {
        var row = await db.TaxAssessments.FirstAsync(r => r.Id == id, ct);
        row.Year = a.Year;
        row.Data = Serialize(a);
        db.Entry(row).Property(r => r.Version).OriginalValue = version;
        // Always written, so that xmin moves even when only the declarants changed.
        db.Entry(row).Property(r => r.Data).IsModified = true;
        // By person, so that a swap of ranks never touches the unique index; a person who left is deleted before one who joins is inserted.
        var rows = await db.TaxAssessmentDeclarants.Where(d => d.AssessmentId == id).ToListAsync(ct);
        db.TaxAssessmentDeclarants.RemoveRange(rows.Where(r => a.Declarants.All(d => d.Person.Value != r.PersonId)));
        foreach (var d in a.Declarants)
            if (rows.FirstOrDefault(r => r.PersonId == d.Person.Value) is { } kept)
                (kept.Year, kept.Rank) = (a.Year, d.Rank);
            else
                db.TaxAssessmentDeclarants.Add(Declarant(id, a.Year, d));
        await SaveAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        // The declarants go with it (cascade); the audit interceptor sees them as tracked deletes, so load them.
        db.TaxAssessmentDeclarants.RemoveRange(await db.TaxAssessmentDeclarants.Where(d => d.AssessmentId == id).ToListAsync(ct));
        db.TaxAssessments.RemoveRange(await db.TaxAssessments.Where(r => r.Id == id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlySet<Guid>?> DeclarantsAsync(Guid assessmentId, CancellationToken ct)
    {
        var persons = await db.TaxAssessmentDeclarants.AsNoTracking().Where(d => d.AssessmentId == assessmentId).Select(d => d.PersonId).ToListAsync(ct);
        return persons.Count == 0 ? null : persons.ToHashSet();
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            throw new TaxAssessmentChangedException();
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            throw new TaxAssessmentTakenException();
        }
    }

    private static TaxAssessmentDeclarantRow Declarant(Guid id, int year, TaxAssessmentDeclarant d)
        => new() { AssessmentId = id, PersonId = d.Person.Value, Year = year, Rank = d.Rank };

    private static string Serialize(TaxAssessment a) => JsonSerializer.Serialize(a, TaxAssessmentJson.Default.TaxAssessment);

    private static StoredTaxAssessment ToStored(TaxAssessmentRow row)
        => new(row.Id, row.Version, JsonSerializer.Deserialize(row.Data, TaxAssessmentJson.Default.TaxAssessment)!);
}
