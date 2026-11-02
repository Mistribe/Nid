using System.Globalization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.RealEstate.Application;

/// <summary>Spec 3C1 §4: <c>scope</c> is the Foyer / Moi selector.</summary>
public sealed record ListPropertiesQuery(VisibilityScope Scope) : IQuery<PropertyList>;

public sealed record GetPropertyQuery(ItemId ItemId) : IQuery<PropertyDetail>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

/// <summary>Spec 3C1 §4: open to every adult (<c>ICreatesItem</c>); owners, visibility and currency never change afterwards.</summary>
public sealed record CreatePropertyCommand(
    string Name, Currency Currency, Visibility Visibility, IReadOnlyList<OwnerShare> Owners, PropertyDetails Details,
    ShareLevel ShareLevel = ShareLevel.View) : ICommand<ItemId>, ICreatesItem;

/// <summary>A PATCH: a null field is left as it is; <c>Clear*</c> empties an optional one.</summary>
public sealed record PropertyChange(
    PropertyType? Type = null, LocalDate? PurchaseDate = null, decimal? PurchasePrice = null, decimal? NotaryFees = null,
    decimal? AgencyFees = null, decimal? OtherAcquisitionCosts = null, decimal? SurfaceM2 = null, bool ClearSurfaceM2 = false,
    string? LocationLabel = null, bool ClearLocationLabel = false, PropertyCadence? Cadence = null, int? StaleAfterDays = null,
    bool ClearStaleAfterDays = false, LocalDate? RentalStartsOn = null, bool ClearRentalStartsOn = false, string? Note = null,
    bool ClearNote = false, string? EnergyClass = null, bool ClearEnergyClass = false, string? PostalAddress = null,
    bool ClearPostalAddress = false)
{
    public PropertyDetails ApplyTo(PropertyDetails d) => new(
        Type ?? d.Type, PurchaseDate ?? d.PurchaseDate, PurchasePrice ?? d.PurchasePrice, NotaryFees ?? d.NotaryFees,
        AgencyFees ?? d.AgencyFees, OtherAcquisitionCosts ?? d.OtherAcquisitionCosts,
        ClearSurfaceM2 ? null : SurfaceM2 ?? d.SurfaceM2, ClearLocationLabel ? null : LocationLabel ?? d.LocationLabel,
        Cadence ?? d.Cadence, ClearStaleAfterDays ? null : StaleAfterDays ?? d.StaleAfterDays,
        ClearRentalStartsOn ? null : RentalStartsOn ?? d.RentalStartsOn, ClearNote ? null : Note ?? d.Note,
        ClearEnergyClass ? null : EnergyClass ?? d.EnergyClass, ClearPostalAddress ? null : PostalAddress ?? d.PostalAddress);
}

/// <summary>Spec 3C1 §4: the <paramref name="Version"/> read, and only what changes; the name is the Core item's.</summary>
public sealed record EditPropertyCommand(ItemId ItemId, string? Name, PropertyChange Change, uint Version) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

[RequestHandler<ListPropertiesQuery, PropertyList>]
public sealed class ListPropertiesHandler(
    IVisibleItems visible, IItemRegistry items, IPropertyStore properties, PropertyReading reading, IHouseholdCalendar calendar,
    IAccessPolicy policy, IActorAccessor actor)
    : IRequestHandler<ListPropertiesQuery, PropertyList>
{
    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    public async ValueTask<Result<PropertyList>> HandleAsync(ListPropertiesQuery query, CancellationToken ct = default)
    {
        var person = actor.Current!.PersonId!.Value;
        var ids = await visible.ForAsync(person, query.Scope, ct);
        var today = await calendar.TodayAsync(ct);

        // Batched reads spec §3: the items, the figures and the write checks, each read once for the list.
        var found = await properties.FindManyAsync(ids.ToList(), ct);
        var accesses = await items.FindManyAsync(found.Select(p => p.ItemId).ToList(), ct);
        var listed = found.Where(p => accesses.ContainsKey(p.ItemId)).Select(p => (Property: p, Item: accesses[p.ItemId])).ToList();
        var read = await reading.ReadManyAsync(listed, person, today, ct);
        if (read.TryGetFailure(out var failure))
            return Result.Failure<PropertyList>(failure!);
        read.TryGetValue(out var reads);
        var writable = await policy.CheckManyAsync(person, listed.Select(l => l.Property.ItemId).ToList(), AccessKind.Write, ct);

        var rows = listed.Select(l => PropertyViews.Summary(l.Item, l.Property, reads![l.Property.ItemId].Figures, writable.Contains(l.Property.ItemId)));
        // A stable order: the name alone ties on a duplicate (as the loans' list).
        return Result.Success(new PropertyList(rows.OrderBy(r => r.Name, French).ThenBy(r => r.Id.Value).ToList()));
    }
}

[RequestHandler<GetPropertyQuery, PropertyDetail>]
public sealed class GetPropertyHandler(
    IPropertyStore properties, IItemRegistry items, PropertyReading reading, IHouseholdCalendar calendar, IAccessPolicy policy, IActorAccessor actor,
    IIrlStore irl)
    : IRequestHandler<GetPropertyQuery, PropertyDetail>
{
    public async ValueTask<Result<PropertyDetail>> HandleAsync(GetPropertyQuery query, CancellationToken ct = default)
    {
        var loaded = await PropertyCheck.LoadAsync(properties, items, query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<PropertyDetail>(failure!);
        loaded.TryGetValue(out var found);
        var person = actor.Current!.PersonId!.Value;
        var today = await calendar.TodayAsync(ct);
        var read = await reading.ReadAsync(found.Property, found.Item, person, today, ct);
        if (read.TryGetFailure(out failure))
            return Result.Failure<PropertyDetail>(failure!);
        read.TryGetValue(out var r);
        var canWrite = (await policy.CheckAsync(person, query.ItemId, AccessKind.Write, ct)).IsSuccess;
        return Result.Success(PropertyViews.Detail(found.Item, found.Property, r!, canWrite, today, await irl.ListAsync(ct)));
    }
}

[RequestHandler<CreatePropertyCommand, ItemId>]
public sealed class CreatePropertyHandler(IItemRegistry items, IPropertyStore properties, IHouseholdCalendar calendar, IUnitOfWork unitOfWork)
    : IRequestHandler<CreatePropertyCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(CreatePropertyCommand command, CancellationToken ct = default)
    {
        if ((PropertyRules.Name(command.Name) ?? PropertyRules.Details(command.Details, await calendar.TodayAsync(ct))) is { } refused)
            return Result.Failure<ItemId>(refused);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var created = await items.CreateAsync(
            new NewItem("realestate", "property", command.Name, command.Visibility, command.Currency.Code, command.Owners, command.ShareLevel), ct);
        if (created.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(FieldErrors.At(failure!, "owners")); // disposed without commit: nothing is left
        created.TryGetValue(out var id);
        await properties.CreateAsync(id, command.Details, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(id);
    }
}

[RequestHandler<EditPropertyCommand, ItemId>]
public sealed class EditPropertyHandler(IItemRegistry items, IPropertyStore properties, IHouseholdCalendar calendar, IUnitOfWork unitOfWork)
    : IRequestHandler<EditPropertyCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(EditPropertyCommand command, CancellationToken ct = default)
    {
        if (command.Name is not null && PropertyRules.Name(command.Name) is { } badName)
            return Result.Failure<ItemId>(badName);
        var today = await calendar.TodayAsync(ct);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await PropertyCheck.LockAsync(properties, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var found);
        if (found.Property.Version != command.Version)
            return Result.Failure<ItemId>(LoanCheck.Conflict(PropertyCodes.PropertyConflict, "property"));

        // Precision 5: the property as it would be is checked; a rental start the request did not send answers at the purchase date it moved.
        var change = command.Change;
        var details = change.ApplyTo(found.Property.Details);
        var rentalField = change.PurchaseDate is not null && change.RentalStartsOn is null ? "purchaseDate" : "rentalStartsOn";
        if (PropertyRules.Details(details, today, rentalField) is { } refused)
            return Result.Failure<ItemId>(refused);
        // Spec 3C2 §2: no tenancy starts before the purchase; tenancy writes take the same row lock, so none slips in between.
        if (found.Property.Tenancies.Count > 0 && details.PurchaseDate > found.Property.Tenancies.Min(t => t.Tenancy.StartsOn))
            return Result.Failure<ItemId>(PropertyRules.Invalid("purchaseDate", "A tenancy of this property starts before this purchase date."));

        // The name is the Core item's: renamed as a loan is, visibility and owners kept.
        var item = found.Item;
        if (command.Name is { } name && name != item.DisplayName)
        {
            var renamed = await items.ReplaceAsync(command.ItemId, name, item.Visibility,
                item.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList(), ct);
            if (renamed.TryGetFailure(out failure))
                return Result.Failure<ItemId>(failure!);
        }

        await properties.UpdateAsync(command.ItemId, details, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }
}
