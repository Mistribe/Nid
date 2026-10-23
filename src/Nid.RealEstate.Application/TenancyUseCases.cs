using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.RealEstate.Application;

public sealed record AddPropertyTenancyCommand(ItemId ItemId, Tenancy Tenancy) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>A correction in place — ending a tenancy is one, with an end (spec 3C2 §3); <paramref name="Version"/> is the tenancy's as read.</summary>
public sealed record EditPropertyTenancyCommand(ItemId ItemId, Guid TenancyId, Tenancy Values, uint Version, bool? Indexed = null, int? IrlReferenceQuarter = null)
    : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record DeletePropertyTenancyCommand(ItemId ItemId, Guid TenancyId, uint Version) : ICommand<bool>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

internal static class TenancyCheck
{
    /// <summary>Spec 3C2 §2: its fields against the stored purchase, then no overlap with the property's other tenancies (<paramref name="self"/> left out).</summary>
    public static Failure? Fits(StoredProperty property, Tenancy tenancy, Guid? self)
        => PropertyRules.TenancyFields(tenancy, property.Details.PurchaseDate)
           ?? PropertyRules.Overlap(tenancy, property.Tenancies.Where(t => t.Id != self).Select(t => t.Tenancy));

    /// <summary>Spec 3C2 §3: a tenancy of this property (another's answers as missing), read since — in that order.</summary>
    public static Failure? Writable(StoredProperty property, Guid id, uint version)
    {
        var tenancy = property.Tenancies.FirstOrDefault(t => t.Id == id);
        if (tenancy is null)
            return new NotFoundFailure("tenancy", id.ToString());
        return tenancy.Version != version ? LoanCheck.Conflict(PropertyCodes.TenancyConflict, "tenancy") : null;
    }

    /// <summary>
    /// Rental spec §3.2: a tenancy with a successor keeps its rent and dates; one that continues another keeps its start (undo, then
    /// revise again) and is not deleted by hand.
    /// </summary>
    public static Failure? Revised(StoredProperty property, Guid id, Tenancy? values)
    {
        var stored = property.Tenancies.First(t => t.Id == id);
        var succeeded = property.Tenancies.Any(t => t.RevisedFrom == id);
        if (values is null)
            return stored.RevisedFrom is not null || succeeded ? Locked(null) : null;
        var old = stored.Tenancy;
        var changed = succeeded && values.RentMonthly != old.RentMonthly ? "rentMonthly"
            : (succeeded || stored.RevisedFrom is not null) && values.StartsOn != old.StartsOn ? "startsOn"
            : succeeded && values.EndsOn != old.EndsOn ? "endsOn" : null;
        return changed is null ? null : Locked(changed);
    }

    private static Failure Locked(string? field)
        => new(PropertyCodes.TenancyRevised, "A revised tenancy keeps its rent and dates, and a revision is only removed by undoing it.",
            field is null ? null : new Dictionary<string, object?> { ["field"] = field });
}

/// <summary>Spec 3C2 §2: the property's row is locked before the overlap is checked, so two concurrent writes cannot both pass.</summary>
[RequestHandler<AddPropertyTenancyCommand, Guid>]
public sealed class AddPropertyTenancyHandler(IItemRegistry items, IPropertyStore properties, IUnitOfWork unitOfWork)
    : IRequestHandler<AddPropertyTenancyCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(AddPropertyTenancyCommand command, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await PropertyCheck.LockAsync(properties, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        loaded.TryGetValue(out var found);
        if (TenancyCheck.Fits(found.Property, command.Tenancy, null) is { } refused)
            return Result.Failure<Guid>(refused);

        var id = Guid.CreateVersion7();
        await properties.AddTenancyAsync(command.ItemId, id, command.Tenancy, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(id);
    }
}

[RequestHandler<EditPropertyTenancyCommand, Guid>]
public sealed class EditPropertyTenancyHandler(IItemRegistry items, IPropertyStore properties, IUnitOfWork unitOfWork)
    : IRequestHandler<EditPropertyTenancyCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(EditPropertyTenancyCommand command, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await PropertyCheck.LockAsync(properties, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        loaded.TryGetValue(out var found);
        if (TenancyCheck.Writable(found.Property, command.TenancyId, command.Version) is { } unwritable)
            return Result.Failure<Guid>(unwritable);

        // Indexation is tri-state on a PUT: absent keeps what is stored, present overrides; the rules see the merged values.
        var stored = found.Property.Tenancies.First(t => t.Id == command.TenancyId).Tenancy;
        var values = command.Values with
        {
            Indexed = command.Indexed ?? stored.Indexed, IrlReferenceQuarter = command.IrlReferenceQuarter ?? stored.IrlReferenceQuarter,
        };
        if ((TenancyCheck.Revised(found.Property, command.TenancyId, values)
             ?? TenancyCheck.Fits(found.Property, values, command.TenancyId)) is { } refused)
            return Result.Failure<Guid>(refused);

        await properties.UpdateTenancyAsync(command.ItemId, command.TenancyId, values, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(command.TenancyId);
    }
}

[RequestHandler<DeletePropertyTenancyCommand, bool>]
public sealed class DeletePropertyTenancyHandler(IItemRegistry items, IPropertyStore properties, IUnitOfWork unitOfWork)
    : IRequestHandler<DeletePropertyTenancyCommand, bool>
{
    public async ValueTask<Result<bool>> HandleAsync(DeletePropertyTenancyCommand command, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await PropertyCheck.LockAsync(properties, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<bool>(failure!);
        loaded.TryGetValue(out var found);
        if ((TenancyCheck.Writable(found.Property, command.TenancyId, command.Version)
             ?? TenancyCheck.Revised(found.Property, command.TenancyId, null)) is { } unwritable)
            return Result.Failure<bool>(unwritable);

        await properties.DeleteTenancyAsync(command.ItemId, command.TenancyId, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(true);
    }
}
