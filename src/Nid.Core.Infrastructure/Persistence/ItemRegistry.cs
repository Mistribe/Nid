using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Core.Domain;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Infrastructure.Persistence;

public sealed class ItemRegistry(CoreDbContext db, IClock clock) : IItemRegistry
{
    public async Task<Result<ItemId>> CreateAsync(NewItem item, CancellationToken ct)
    {
        var personIds = item.Owners.Select(o => o.PersonId.Value).ToList();
        var kinds = await db.Persons.AsNoTracking()
            .Where(p => personIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Kind == "adult" ? PersonKind.Adult : PersonKind.Dependent, ct);

        if (kinds.Count != personIds.Distinct().Count())
            return Result.Failure<ItemId>(new Failure("core.owner_unknown", "An owner does not exist.", null));

        var owners = item.Owners.Select(o => new Owner(o.PersonId, kinds[o.PersonId.Value], o.ShareBp)).ToList();
        var validation = OwnershipRules.Validate(item.Visibility, owners);
        if (validation.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);

        var id = ItemId.New();
        db.Items.Add(new ItemRow
        {
            Id = id.Value,
            Module = item.Module,
            Kind = item.Kind,
            DisplayName = item.DisplayName,
            Visibility = item.Visibility == Visibility.Private ? "private" : "shared",
            Currency = item.Currency,
            CreatedAt = clock.GetCurrentInstant(),
            Owners = owners.Select(o => new OwnershipRow { ItemId = id.Value, PersonId = o.PersonId.Value, ShareBp = o.ShareBp }).ToList(),
        });
        await db.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<ItemAccess?> FindAsync(ItemId id, CancellationToken ct)
    {
        var row = await db.Items.AsNoTracking().Include(i => i.Owners).SingleOrDefaultAsync(i => i.Id == id.Value, ct);
        if (row is null)
            return null;

        var kinds = await db.Persons.AsNoTracking()
            .Where(p => row.Owners.Select(o => o.PersonId).Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Kind == "adult" ? PersonKind.Adult : PersonKind.Dependent, ct);

        return new ItemAccess(
            id,
            row.Visibility == "private" ? Visibility.Private : Visibility.Shared,
            row.Owners.Select(o => new Owner(new PersonId(o.PersonId), kinds[o.PersonId], o.ShareBp)).ToList());
    }
}
