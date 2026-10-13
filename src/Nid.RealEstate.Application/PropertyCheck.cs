using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.RealEstate.Application;

internal static class PropertyCheck
{
    /// <summary>The property and its item; an item that is not a property (a loan, an envelope) answers as a missing property (spec 3C1 §4).</summary>
    public static async Task<Result<(StoredProperty Property, ItemAccess Item)>> LoadAsync(
        IPropertyStore properties, IItemRegistry items, ItemId id, CancellationToken ct)
    {
        var property = await properties.FindAsync(id, ct);
        var item = property is null ? null : await items.FindAsync(id, ct);
        return property is null || item is null
            ? Result.Failure<(StoredProperty, ItemAccess)>(new NotFoundFailure("property", id.ToString()))
            : Result.Success((property, item));
    }

    /// <summary>Precision 4: a PATCH locks the property's row for its transaction, then reads it as it now is.</summary>
    public static async Task<Result<(StoredProperty Property, ItemAccess Item)>> LockAsync(
        IPropertyStore properties, IItemRegistry items, ItemId id, CancellationToken ct)
    {
        await properties.LockAsync(id, ct);
        return await LoadAsync(properties, items, id, ct);
    }
}
