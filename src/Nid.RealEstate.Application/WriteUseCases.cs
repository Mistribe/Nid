using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.RealEstate.Application;

/// <summary>
/// Spec 3A2 §4: open to every adult; the pipeline checks the borrowers as an item's creators (<c>ICreatesItem</c>).
/// <paramref name="PropertyId"/> is the property it finances (spec 3C1 §1.5), checked by <see cref="PropertyCheck.LinkableAsync"/>.
/// </summary>
public sealed record CreateLoanCommand(
    string Name, Currency Currency, Visibility Visibility, IReadOnlyList<OwnerShare> Owners, string? Lender, string? Note,
    LoanContract Contract, ItemId? PropertyId = null) : ICommand<ItemId>, ICreatesItem;

/// <summary>Spec 3A2 §4: name, lender and note, only what is sent; borrowers, visibility and currency are set at creation.</summary>
public sealed record EditLoanCommand(ItemId ItemId, string? Name, LoanDetailsChange Change) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>Spec 3A2 §1: the terms, phases and insurances replaced whole; <paramref name="Version"/> is the contract's as read.</summary>
public sealed record ReplaceLoanContractCommand(ItemId ItemId, LoanContract Contract, uint Version) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

[RequestHandler<CreateLoanCommand, ItemId>]
public sealed class CreateLoanHandler(
    IItemRegistry items, ILoanStore loans, IPropertyStore properties, IAccessPolicy policy, IActorAccessor actor, IUnitOfWork unitOfWork)
    : IRequestHandler<CreateLoanCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(CreateLoanCommand command, CancellationToken ct = default)
    {
        if ((LoanRules.Name(command.Name) ?? LoanRules.Details(command.Lender, command.Note) ?? LoanRules.Contract(command.Contract)) is { } refused)
            return Result.Failure<ItemId>(refused);
        var built = LoanScheduleBuilder.Build(LoanReading.Terms(command.Currency, command.Contract, [], []));
        if (built.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(LoanPaths.ForContract(failure!));
        if (await PropertyCheck.LinkableAsync(properties, policy, actor.Current!.PersonId!.Value, command.PropertyId, ct) is { } unlinkable)
            return Result.Failure<ItemId>(unlinkable);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var created = await items.CreateAsync(
            new NewItem("realestate", "loan", command.Name, command.Visibility, command.Currency.Code, command.Owners), ct);
        if (created.TryGetFailure(out failure))
            return Result.Failure<ItemId>(FieldErrors.At(failure!, "borrowers")); // disposed without commit: nothing is left
        created.TryGetValue(out var id);
        await loans.CreateAsync(id, command.Lender, command.Note, command.PropertyId, command.Contract, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(id);
    }
}

[RequestHandler<EditLoanCommand, ItemId>]
public sealed class EditLoanHandler(
    IItemRegistry items, ILoanStore loans, IPropertyStore properties, IAccessPolicy policy, IActorAccessor actor, IUnitOfWork unitOfWork)
    : IRequestHandler<EditLoanCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(EditLoanCommand command, CancellationToken ct = default)
    {
        var change = command.Change;
        if (((command.Name is null ? null : LoanRules.Name(command.Name)) ?? LoanRules.Details(change.Lender, change.Note)) is { } refused)
            return Result.Failure<ItemId>(refused);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await LoanCheck.LockAsync(loans, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var found);
        var item = found.Item;

        // Spec 3C1 §3: linking needs the property's read access (the loan's write access is the pipeline's); clearing needs nothing more.
        if (!change.ClearProperty
            && await PropertyCheck.LinkableAsync(properties, policy, actor.Current!.PersonId!.Value, change.PropertyId, ct) is { } unlinkable)
            return Result.Failure<ItemId>(unlinkable);

        // The name is the Core item's: renamed as EditIncomeSourceHandler renames a source, visibility and borrowers kept.
        if (command.Name is { } name && name != item.DisplayName)
        {
            var renamed = await items.ReplaceAsync(command.ItemId, name, item.Visibility,
                item.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList(), ct);
            if (renamed.TryGetFailure(out failure))
                return Result.Failure<ItemId>(failure!);
        }

        await loans.UpdateDetailsAsync(command.ItemId, change, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }
}

[RequestHandler<ReplaceLoanContractCommand, ItemId>]
public sealed class ReplaceLoanContractHandler(IItemRegistry items, ILoanStore loans, IUnitOfWork unitOfWork)
    : IRequestHandler<ReplaceLoanContractCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(ReplaceLoanContractCommand command, CancellationToken ct = default)
    {
        if (LoanRules.Contract(command.Contract) is { } refused)
            return Result.Failure<ItemId>(refused);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await LoanCheck.LockAsync(loans, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var found);
        if (found.Loan.Version != command.Version)
            return Result.Failure<ItemId>(LoanCheck.Conflict(LoanCodes.LoanConflict, "loan"));

        // Rebuilt with the stored fees and repayments (spec §4): a repayment the new contract makes impossible refuses it.
        var built = LoanScheduleBuilder.Build(
            LoanReading.Terms(LoanViews.CurrencyOf(found.Item), command.Contract, found.Loan.Fees, found.Loan.Repayments));
        if (built.TryGetFailure(out failure))
            return Result.Failure<ItemId>(LoanPaths.ForContract(failure!));

        await loans.ReplaceContractAsync(command.ItemId, command.Contract, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }
}
