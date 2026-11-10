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
            ShareLevel = Level(item.Visibility, item.ShareLevel),
            Currency = item.Currency,
            CreatedAt = clock.GetCurrentInstant(),
            Owners = owners!.Select(o => new OwnershipRow { ItemId = id.Value, PersonId = o.PersonId.Value, ShareBp = o.ShareBp }).ToList(),
        });
        await db.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<Result> ReplaceAsync(ItemId id, string displayName, Visibility visibility, IReadOnlyList<OwnerShare> owners, CancellationToken ct,
        ShareLevel? shareLevel = null)
    {
        var validated = await ValidateOwnersAsync(visibility, owners, ct);
        if (validated.TryGetFailure(out var failure))
            return Result.Failure(failure!);

        var row = await db.Items.Include(i => i.Owners).FirstOrDefaultAsync(i => i.Id == id.Value, ct);
        if (row is null)
            return Result.Failure(new NotFoundFailure("item", id.ToString()));

        row.DisplayName = displayName;
        row.Visibility = visibility == Visibility.Private ? "private" : "shared";
        row.ShareLevel = Level(visibility, shareLevel ?? (row.ShareLevel == "edit" ? ShareLevel.Edit : ShareLevel.View));
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

    public async Task<Result> SetShareLevelAsync(ItemId id, ShareLevel level, CancellationToken ct)
    {
        var row = await db.Items.SingleOrDefaultAsync(i => i.Id == id.Value, ct);
        if (row is null)
            return Result.Failure(new NotFoundFailure("item", id.ToString()));
        if (row.Visibility == "private" && level == ShareLevel.Edit)
            return Result.Failure(new Failure(ItemCodes.PrivateHasNoLevel, "A private item has no sharing level.", null));

        row.ShareLevel = Level(Visibility.Shared, level);
        await db.SaveChangesAsync(ct); // a tracked update: the write audit records share_level
        return Result.Success();
    }

    /// <summary>A private item has no level: it is stored as <c>view</c>.</summary>
    private static string Level(Visibility visibility, ShareLevel level)
        => visibility == Visibility.Shared && level == ShareLevel.Edit ? "edit" : "view";

    public async Task<ItemAccess?> FindAsync(ItemId id, CancellationToken ct)
    {
        var row = await db.Items.AsNoTracking().Include(i => i.Owners).SingleOrDefaultAsync(i => i.Id == id.Value, ct);
        if (row is null)
            return null;

        var kinds = await db.Persons.AsNoTracking()
            .Where(p => row.Owners.Select(o => o.PersonId).Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Kind == "adult" ? PersonKind.Adult : PersonKind.Dependent, ct);

        return ToAccess(row, kinds);
    }

    public async Task<IReadOnlyDictionary<ItemId, ItemAccess>> FindManyAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return new Dictionary<ItemId, ItemAccess>();

        var keys = ids.Select(i => i.Value).Distinct().ToList();
        return (await ReadAsync(db.Items.AsNoTracking().Where(i => keys.Contains(i.Id)), ct)).ToDictionary(a => a.Id);
    }

    public async Task<IReadOnlyList<ItemAccess>> SearchAsync(IReadOnlyCollection<ItemId> ids, string module, string kind, string? nameContains, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];

        var keys = ids.Select(i => i.Value).Distinct().ToList();
        var items = db.Items.AsNoTracking().Where(i => keys.Contains(i.Id) && i.Module == module && i.Kind == kind);
        // ponytail: ILIKE ignores case, not accents (SeriesSearch.Matches ignores both); upgrade with unaccent if the household asks.
        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            // Typed text, not a pattern: % and _ match themselves.
            var pattern = LikePattern.Contains(nameContains);
            items = items.Where(i => EF.Functions.ILike(i.DisplayName, pattern, @"\"));
        }

        return await ReadAsync(items, ct);
    }

    private async Task<List<ItemAccess>> ReadAsync(IQueryable<ItemRow> items, CancellationToken ct)
    {
        var rows = await items.Include(i => i.Owners).ToListAsync(ct);
        var owners = rows.SelectMany(r => r.Owners.Select(o => o.PersonId)).Distinct().ToList();
        var kinds = await db.Persons.AsNoTracking()
            .Where(p => owners.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Kind == "adult" ? PersonKind.Adult : PersonKind.Dependent, ct);

        return rows.Select(r => ToAccess(r, kinds)).ToList();
    }

    private static ItemAccess ToAccess(ItemRow row, IReadOnlyDictionary<Guid, PersonKind> kinds)
        => new(
            new ItemId(row.Id),
            row.DisplayName,
            row.Visibility == "private" ? Visibility.Private : Visibility.Shared,
            row.Owners.Select(o => new Owner(new PersonId(o.PersonId), kinds[o.PersonId], o.ShareBp)).ToList())
        {
            ShareLevel = row.ShareLevel == "edit" ? ShareLevel.Edit : ShareLevel.View,
            ArchivedAt = row.ArchivedAt,
            Currency = row.Currency?.Trim(),
            Module = row.Module,
            Kind = row.Kind,
            Version = row.Version,
        };

    public Task<Result> ArchiveAsync(ItemId id, Instant at, CancellationToken ct) => SetArchivedAsync(id, at, ct);

    public Task<Result> UnarchiveAsync(ItemId id, CancellationToken ct) => SetArchivedAsync(id, null, ct);

    private async Task<Result> SetArchivedAsync(ItemId id, Instant? at, CancellationToken ct)
    {
        var row = await db.Items.SingleOrDefaultAsync(i => i.Id == id.Value, ct);
        if (row is null)
            return Result.Failure(new NotFoundFailure("item", id.ToString()));

        row.ArchivedAt = at;
        await db.SaveChangesAsync(ct); // a tracked update: the write audit records archived_at
        return Result.Success();
    }

    public async Task<Result> DeleteAsync(ItemId id, uint version, CancellationToken ct)
    {
        // Owners loaded: their removes are tracked, so audited, rather than left to the database cascade.
        var row = await db.Items.Include(i => i.Owners).SingleOrDefaultAsync(i => i.Id == id.Value, ct);
        if (row is null)
            return Result.Failure(new NotFoundFailure("item", id.ToString()));

        db.Entry(row).Property(r => r.Version).OriginalValue = version;
        db.Items.Remove(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(new Failure(ItemCodes.Changed, "The item changed since it was read.", null));
        }
    }
}
