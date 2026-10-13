using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.RealEstate.Application;

/// <summary>A planned cost as a body sends it (spec 3C2 §3): its category and its period are checked by the rules, at the body's fields.</summary>
public sealed record RecurringCostDraft(
    string? Category, decimal Amount, int EveryCount, DurationUnit EveryUnit, LocalDate StartsOn, LocalDate? EndsOn, string? Note)
{
    /// <summary>Once checked: a count of 1 to 120 always makes the Core's period.</summary>
    public RecurringCost ToCost()
        => new(Category!, Amount,
            Duration.Create(EveryCount, EveryUnit).TryGetValue(out var every) ? every : throw new InvalidOperationException("A checked cost has a positive count."),
            StartsOn, EndsOn, Note);
}

public sealed record AddPropertyRecurringCostCommand(ItemId ItemId, RecurringCostDraft Cost) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>A correction in place; <paramref name="Version"/> is the cost's as the client read it.</summary>
public sealed record EditPropertyRecurringCostCommand(ItemId ItemId, Guid Cost, RecurringCostDraft Values, uint Version) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record DeletePropertyRecurringCostCommand(ItemId ItemId, Guid Cost, uint Version) : ICommand<bool>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

internal static class CostCheck
{
    /// <summary>Spec 3C2 §3: a cost of this property (another's answers as missing), read since — in that order.</summary>
    public static Failure? Writable(StoredProperty property, Guid id, uint version)
    {
        var cost = property.Costs.FirstOrDefault(c => c.Id == id);
        if (cost is null)
            return new NotFoundFailure("recurring cost", id.ToString());
        return cost.Version != version ? LoanCheck.Conflict(PropertyCodes.RecurringCostConflict, "recurring cost") : null;
    }
}

/// <summary>Precision 5: a cost write locks the property's row, as a fee write locks its loan's.</summary>
[RequestHandler<AddPropertyRecurringCostCommand, Guid>]
public sealed class AddPropertyRecurringCostHandler(IItemRegistry items, IPropertyStore properties, IUnitOfWork unitOfWork)
    : IRequestHandler<AddPropertyRecurringCostCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(AddPropertyRecurringCostCommand command, CancellationToken ct = default)
    {
        if (PropertyRules.CostFields(command.Cost) is { } refused)
            return Result.Failure<Guid>(refused);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await PropertyCheck.LockAsync(properties, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);

        var id = Guid.CreateVersion7();
        await properties.AddCostAsync(command.ItemId, id, command.Cost.ToCost(), ct);
        await transaction.CommitAsync(ct);
        return Result.Success(id);
    }
}

[RequestHandler<EditPropertyRecurringCostCommand, Guid>]
public sealed class EditPropertyRecurringCostHandler(IItemRegistry items, IPropertyStore properties, IUnitOfWork unitOfWork)
    : IRequestHandler<EditPropertyRecurringCostCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(EditPropertyRecurringCostCommand command, CancellationToken ct = default)
    {
        if (PropertyRules.CostFields(command.Values) is { } refused)
            return Result.Failure<Guid>(refused);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await PropertyCheck.LockAsync(properties, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        loaded.TryGetValue(out var found);
        if (CostCheck.Writable(found.Property, command.Cost, command.Version) is { } unwritable)
            return Result.Failure<Guid>(unwritable);

        await properties.UpdateCostAsync(command.ItemId, command.Cost, command.Values.ToCost(), ct);
        await transaction.CommitAsync(ct);
        return Result.Success(command.Cost);
    }
}

[RequestHandler<DeletePropertyRecurringCostCommand, bool>]
public sealed class DeletePropertyRecurringCostHandler(IItemRegistry items, IPropertyStore properties, IUnitOfWork unitOfWork)
    : IRequestHandler<DeletePropertyRecurringCostCommand, bool>
{
    public async ValueTask<Result<bool>> HandleAsync(DeletePropertyRecurringCostCommand command, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await PropertyCheck.LockAsync(properties, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<bool>(failure!);
        loaded.TryGetValue(out var found);
        if (CostCheck.Writable(found.Property, command.Cost, command.Version) is { } unwritable)
            return Result.Failure<bool>(unwritable);

        await properties.DeleteCostAsync(command.ItemId, command.Cost, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(true);
    }
}
