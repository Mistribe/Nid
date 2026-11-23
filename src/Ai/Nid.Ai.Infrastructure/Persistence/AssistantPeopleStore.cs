using Microsoft.EntityFrameworkCore;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Ai.Infrastructure.Persistence;

public sealed class AssistantPeopleStore(AiDbContext db) : IAssistantPeople
{
    public async Task<(bool Enabled, string OffsiteDefault)> GetAsync(PersonId person, CancellationToken ct)
        => await db.AssistantPeople.AsNoTracking().SingleOrDefaultAsync(p => p.PersonId == person.Value, ct) is { } row
            ? (row.Enabled, row.OffsiteDefault)
            : (false, OffsitePreferences.Ask);

    public Task SetEnabledAsync(PersonId person, bool enabled, Instant at, CancellationToken ct)
        => ChangeAsync(person, row => row.Enabled = enabled, at, ct);

    public Task SetOffsiteDefaultAsync(PersonId person, string preference, Instant at, CancellationToken ct)
        => ChangeAsync(person, row => row.OffsiteDefault = preference, at, ct);

    public async Task<IReadOnlySet<PersonId>> NeverAsync(CancellationToken ct)
        => (await db.AssistantPeople.AsNoTracking().Where(p => p.OffsiteDefault == OffsitePreferences.Never).Select(p => p.PersonId).ToListAsync(ct))
            .Select(id => new PersonId(id)).ToHashSet();

    private async Task ChangeAsync(PersonId person, Action<AssistantPersonRow> change, Instant at, CancellationToken ct)
    {
        var row = await db.AssistantPeople.SingleOrDefaultAsync(p => p.PersonId == person.Value, ct);
        if (row is null)
            db.AssistantPeople.Add(row = new AssistantPersonRow { PersonId = person.Value });
        change(row);
        row.ChangedAt = at;
        await db.SaveChangesAsync(ct);
    }
}
