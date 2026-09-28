using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Core.Domain;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Infrastructure.Persistence;

public sealed class ItemRegistry(CoreDbContext db, IClock clock) : IItemRegistry
{
    /// <summary>
    /// The owner-kind lookup, the <c>core.owner_unknown</c> failure and <see cref="OwnershipRules.Validate"/> that
    /// both <see cref="CreateAsync"/> and <see cref="ReplaceAsync"/> need before they touch a row.
    /// </summary>
    private async Task<Result<List<Owner>>> ValidateOwnersAsync(Visibility visibility, IReadOnlyList<OwnerShare> owners, CancellationToken ct)
    {
        var personIds = owners.Select(o => o.PersonId.Value).ToList();
        var kinds = await db.Persons.AsNoTracking()
            .Where(p => personIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Kind == "adult" ? PersonKind.Adult : PersonKind.Dependent, ct);

        if (kinds.Count != personIds.Distinct().Count())
            return Result.Failure<List<Owner>>(new Failure("core.owner_unknown", "An owner does not exist.", null));

        var ownerList = owners.Select(o => new Owner(o.PersonId, kinds[o.PersonId.Value], o.ShareBp)).ToList();
        var validation = OwnershipRules.Validate(visibility, ownerList);
        return validation.TryGetFailure(out var failure) ? Result.Failure<List<Owner>>(failure!) : Result.Success(ownerList);
    }

    public async Task<Result<ItemId>> CreateAsync(NewItem item, CancellationToken ct)
    {
        var validated = await ValidateOwnersAsync(item.Visibility, item.Owners, ct);
        if (validated.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        validated.TryGetValue(out var owners);

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
            Owners = owners!.Select(o => new OwnershipRow { ItemId = id.Value, PersonId = o.PersonId.Value, ShareBp = o.ShareBp }).ToList(),
        });
        await db.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<Result> ReplaceAsync(ItemId id, string displayName, Visibility visibility, IReadOnlyList<OwnerShare> owners, CancellationToken ct)
    {
        var validated = await ValidateOwnersAsync(visibility, owners, ct);
        if (validated.TryGetFailure(out var failure))
            return Result.Failure(failure!);

        var row = await db.Items.Include(i => i.Owners).FirstOrDefaultAsync(i => i.Id == id.Value, ct);
        if (row is null)
            return Result.Failure(new NotFoundFailure("item", id.ToString()));

        row.DisplayName = displayName;
        row.Visibility = visibility == Visibility.Private ? "private" : "shared";
        var wanted = owners.ToDictionary(o => o.PersonId.Value, o => o.ShareBp);
        foreach (var owner in row.Owners.Where(o => !wanted.ContainsKey(o.PersonId)).ToList())
            row.Owners.Remove(owner);
        foreach (var owner in row.Owners)
            owner.ShareBp = wanted[owner.PersonId];
        foreach (var (person, share) in wanted.Where(w => row.Owners.All(o => o.PersonId != w.Key)))
            row.Owners.Add(new OwnershipRow { ItemId = id.Value, PersonId = person, ShareBp = share });

        await db.SaveChangesAsync(ct);
        return Result.Success();
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
            row.DisplayName,
            row.Visibility == "private" ? Visibility.Private : Visibility.Shared,
            row.Owners.Select(o => new Owner(new PersonId(o.PersonId), kinds[o.PersonId], o.ShareBp)).ToList());
    }
}
