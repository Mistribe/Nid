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
public sealed record EditPropertyTenancyCommand(ItemId ItemId, Guid TenancyId, Tenancy Values, uint Version) : ICommand<Guid>, IItemScoped
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
        if ((TenancyCheck.Writable(found.Property, command.TenancyId, command.Version)
             ?? TenancyCheck.Fits(found.Property, command.Values, command.TenancyId)) is { } refused)
            return Result.Failure<Guid>(refused);

        await properties.UpdateTenancyAsync(command.ItemId, command.TenancyId, command.Values, ct);
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
        if (TenancyCheck.Writable(found.Property, command.TenancyId, command.Version) is { } unwritable)
            return Result.Failure<bool>(unwritable);

        await properties.DeleteTenancyAsync(command.ItemId, command.TenancyId, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(true);
    }
}
