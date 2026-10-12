using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.RealEstate.Application;

/// <summary>A fee as a body sends it (spec 3A2 §4): no id and no version yet.</summary>
public sealed record FeeDraft(FeeKind Kind, decimal Amount, LocalDate OnDate, string? Note);

public sealed record AddLoanFeeCommand(ItemId ItemId, FeeDraft Fee) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>A correction in place; <paramref name="Version"/> is the fee's as the client read it.</summary>
public sealed record EditLoanFeeCommand(ItemId ItemId, Guid Fee, FeeDraft Values, uint Version) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record DeleteLoanFeeCommand(ItemId ItemId, Guid Fee, uint Version) : ICommand<bool>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

internal static class FeeCheck
{
    /// <summary>The engine's fee rules (a known kind, a positive amount) and spec §3's two decimals, at the fee route's fields.</summary>
    public static Failure? Rules(FeeDraft draft)
        => !Enum.IsDefined(draft.Kind) ? LoanRules.Invalid("kind", "The fee kind is unknown.")
            : draft.Amount <= 0m ? LoanRules.Invalid("amount", "A fee is positive.")
            : LoanRules.Amount(draft.Amount, "amount") ?? LoanRules.InYears(draft.OnDate, "onDate") ?? LoanRules.Note(draft.Note, "note");

    public static LoanFee Fee(FeeDraft draft) => new(draft.Kind, draft.Amount, draft.OnDate);

    /// <summary>Spec §4: a fee is missing, a repayment's penalty (changed through it only), or read since — in that order.</summary>
    public static Failure? Writable(StoredLoan loan, Guid id, uint version)
    {
        var fee = loan.Fees.FirstOrDefault(f => f.Id == id);
        if (fee is null)
            return new NotFoundFailure("loan fee", id.ToString());
        if (loan.Repayments.Any(r => r.FeeId == id))
            return new Failure(LoanCodes.FeeLinked, "This penalty belongs to an early repayment: change or remove it there.",
                new Dictionary<string, object?> { ["field"] = "feeId" });
        return fee.Version != version ? LoanCheck.Conflict(LoanCodes.FeeConflict, "fee") : null;
    }
}

/// <summary>Spec §4: a fee is not a row of the schedule, so a fee write does not rebuild it.</summary>
[RequestHandler<AddLoanFeeCommand, Guid>]
public sealed class AddLoanFeeHandler(IItemRegistry items, ILoanStore loans, IUnitOfWork unitOfWork) : IRequestHandler<AddLoanFeeCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(AddLoanFeeCommand command, CancellationToken ct = default)
    {
        if (FeeCheck.Rules(command.Fee) is { } refused)
            return Result.Failure<Guid>(refused);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await LoanCheck.LockAsync(loans, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);

        var id = Guid.CreateVersion7();
        await loans.AddFeeAsync(command.ItemId, id, FeeCheck.Fee(command.Fee), command.Fee.Note, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(id);
    }
}

[RequestHandler<EditLoanFeeCommand, Guid>]
public sealed class EditLoanFeeHandler(IItemRegistry items, ILoanStore loans, IUnitOfWork unitOfWork) : IRequestHandler<EditLoanFeeCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(EditLoanFeeCommand command, CancellationToken ct = default)
    {
        if (FeeCheck.Rules(command.Values) is { } refused)
            return Result.Failure<Guid>(refused);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await LoanCheck.LockAsync(loans, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        loaded.TryGetValue(out var found);
        if (FeeCheck.Writable(found.Loan, command.Fee, command.Version) is { } unwritable)
            return Result.Failure<Guid>(unwritable);

        await loans.UpdateFeeAsync(command.ItemId, command.Fee, FeeCheck.Fee(command.Values), command.Values.Note, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(command.Fee);
    }
}

[RequestHandler<DeleteLoanFeeCommand, bool>]
public sealed class DeleteLoanFeeHandler(IItemRegistry items, ILoanStore loans, IUnitOfWork unitOfWork) : IRequestHandler<DeleteLoanFeeCommand, bool>
{
    public async ValueTask<Result<bool>> HandleAsync(DeleteLoanFeeCommand command, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await LoanCheck.LockAsync(loans, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<bool>(failure!);
        loaded.TryGetValue(out var found);
        if (FeeCheck.Writable(found.Loan, command.Fee, command.Version) is { } unwritable)
            return Result.Failure<bool>(unwritable);

        await loans.DeleteFeeAsync(command.ItemId, command.Fee, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(true);
    }
}
