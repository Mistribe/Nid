using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.RealEstate.Application;

/// <summary>A property's flow as sent (spec 3C1 §4): its kind and category are checked against the module's convention.</summary>
public sealed record PropertyFlowDraft(LocalDate OccurredOn, string? Kind, decimal Amount, string? Category, string? Note, string? ExternalRef = null);

public sealed record RecordPropertyValuationCommand(ItemId ItemId, LocalDate AsOf, decimal Amount, string? Note) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record VoidPropertyValuationCommand(ItemId ItemId, Guid ValuationId) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record RecordPropertyFlowCommand(ItemId ItemId, PropertyFlowDraft Flow) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record CorrectPropertyFlowCommand(ItemId ItemId, Guid FlowId, PropertyFlowDraft Flow) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record VoidPropertyFlowCommand(ItemId ItemId, Guid FlowId) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>What every fact command shares: the property first (404 for any other item), its money, the Core's date refusal located.</summary>
internal static class PropertyFacts
{
    public static async Task<Result<Currency>> CurrencyAsync(IPropertyStore properties, IItemRegistry items, ItemId id, CancellationToken ct)
    {
        var loaded = await PropertyCheck.LoadAsync(properties, items, id, ct);
        return loaded.TryGetValue(out var found)
            ? Result.Success(LoanViews.CurrencyOf(found.Item))
            : Result.Failure<Currency>(loaded.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
    }

    /// <summary>A checked amount has two decimals: Money's four always hold it.</summary>
    public static Money MoneyOf(decimal amount, Currency currency)
        => Money.Create(amount, currency).TryGetValue(out var money) ? money : throw new InvalidOperationException("A checked amount has two decimals.");

    public static Result<NewFlow> FlowOf(ItemId item, PropertyFlowDraft draft, Currency currency)
        => (PropertyRules.Category(draft.Kind, draft.Category) ?? PropertyRules.Positive(draft.Amount, "amount")) is { } refused
            ? Result.Failure<NewFlow>(refused)
            : Result.Success(new NewFlow(item, draft.OccurredOn, PropertyRules.KindOf(draft.Kind!), MoneyOf(draft.Amount, currency),
                draft.Category, draft.ExternalRef, draft.Note));

    /// <summary>Spec 3C1 §4: every 422 names its field; the Core's date refusal names none, so the route's date field is given.</summary>
    public static Result<Guid> Answer<T>(Result<T> written, Func<T, Guid> id, string dateField) where T : notnull
        => written.TryGetValue(out var value)
            ? Result.Success(id(value!))
            : Result.Failure<Guid>(written.TryGetFailure(out var failure) && failure!.Code == CoreFactCodes.DateInFuture
                ? LoanPaths.At(failure, dateField)
                : failure!);
}

[RequestHandler<RecordPropertyValuationCommand, Guid>]
public sealed class RecordPropertyValuationHandler(IPropertyStore properties, IItemRegistry items, IValuations valuations)
    : IRequestHandler<RecordPropertyValuationCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(RecordPropertyValuationCommand command, CancellationToken ct = default)
    {
        var currency = await PropertyFacts.CurrencyAsync(properties, items, command.ItemId, ct);
        if (!currency.TryGetValue(out var c))
            return Result.Failure<Guid>(currency.TryGetFailure(out var missing) ? missing! : throw new InvalidOperationException());
        if (PropertyRules.Positive(command.Amount, "amount") is { } refused)
            return Result.Failure<Guid>(refused);
        var recorded = await valuations.RecordAsync(new NewValuation(command.ItemId, command.AsOf, PropertyFacts.MoneyOf(command.Amount, c), command.Note), ct);
        return PropertyFacts.Answer(recorded, v => v.Id, "asOf");
    }
}

[RequestHandler<VoidPropertyValuationCommand, Guid>]
public sealed class VoidPropertyValuationHandler(IPropertyStore properties, IItemRegistry items, IValuations valuations)
    : IRequestHandler<VoidPropertyValuationCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(VoidPropertyValuationCommand command, CancellationToken ct = default)
    {
        if ((await PropertyFacts.CurrencyAsync(properties, items, command.ItemId, ct)).TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        var voided = await valuations.VoidAsync(command.ItemId, command.ValuationId, ct);
        return voided.TryGetFailure(out failure) ? Result.Failure<Guid>(failure!) : Result.Success(command.ValuationId);
    }
}

[RequestHandler<RecordPropertyFlowCommand, Guid>]
public sealed class RecordPropertyFlowHandler(IPropertyStore properties, IItemRegistry items, IFlows flows)
    : IRequestHandler<RecordPropertyFlowCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(RecordPropertyFlowCommand command, CancellationToken ct = default)
    {
        var currency = await PropertyFacts.CurrencyAsync(properties, items, command.ItemId, ct);
        if (!currency.TryGetValue(out var c))
            return Result.Failure<Guid>(currency.TryGetFailure(out var missing) ? missing! : throw new InvalidOperationException());
        var flow = PropertyFacts.FlowOf(command.ItemId, command.Flow, c);
        if (!flow.TryGetValue(out var f))
            return Result.Failure<Guid>(flow.TryGetFailure(out var refused) ? refused! : throw new InvalidOperationException());
        return PropertyFacts.Answer(await flows.RecordAsync(f!, ct), x => x.Id, "occurredOn");
    }
}

[RequestHandler<CorrectPropertyFlowCommand, Guid>]
public sealed class CorrectPropertyFlowHandler(IPropertyStore properties, IItemRegistry items, IFlows flows)
    : IRequestHandler<CorrectPropertyFlowCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(CorrectPropertyFlowCommand command, CancellationToken ct = default)
    {
        var currency = await PropertyFacts.CurrencyAsync(properties, items, command.ItemId, ct);
        if (!currency.TryGetValue(out var c))
            return Result.Failure<Guid>(currency.TryGetFailure(out var missing) ? missing! : throw new InvalidOperationException());
        var flow = PropertyFacts.FlowOf(command.ItemId, command.Flow, c);
        if (!flow.TryGetValue(out var f))
            return Result.Failure<Guid>(flow.TryGetFailure(out var refused) ? refused! : throw new InvalidOperationException());
        // A correction without a reference keeps the stored one: a bank cash-in's `bank:` reference still refuses a second one (D20).
        if (f!.ExternalRef is null && (await flows.FindManyAsync([command.FlowId], ct)).FirstOrDefault(x => x.Item == command.ItemId) is { ExternalRef: { } kept })
            f = f with { ExternalRef = kept };
        // The Core looks the flow up by (id, item): another property's flow is core.fact_not_found (Review Focus #3).
        return PropertyFacts.Answer(await flows.SupersedeAsync(command.ItemId, command.FlowId, f, ct), x => x.Id, "occurredOn");
    }
}

[RequestHandler<VoidPropertyFlowCommand, Guid>]
public sealed class VoidPropertyFlowHandler(IPropertyStore properties, IItemRegistry items, IFlows flows)
    : IRequestHandler<VoidPropertyFlowCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(VoidPropertyFlowCommand command, CancellationToken ct = default)
    {
        if ((await PropertyFacts.CurrencyAsync(properties, items, command.ItemId, ct)).TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        var voided = await flows.VoidAsync(command.ItemId, command.FlowId, ct);
        return voided.TryGetFailure(out failure) ? Result.Failure<Guid>(failure!) : Result.Success(command.FlowId);
    }
}
