using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.RealEstate.Application;

/// <summary>An early repayment as a body sends it (spec 3A2 §4); <c>Penalty</c>, when sent, becomes its penalty fee.</summary>
public sealed record RepaymentDraft(LocalDate OnDate, decimal Amount, RepaymentEffect Effect, decimal? Penalty, string? Note);

public sealed record AddLoanRepaymentCommand(ItemId ItemId, RepaymentDraft Repayment) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>The same body as the creation: a penalty sent is created or updated, a penalty omitted is deleted.</summary>
public sealed record EditLoanRepaymentCommand(ItemId ItemId, Guid Repayment, RepaymentDraft Values, uint Version) : ICommand<Guid>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>The repayment and its penalty.</summary>
public sealed record DeleteLoanRepaymentCommand(ItemId ItemId, Guid Repayment, uint Version) : ICommand<bool>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

internal static class RepaymentCheck
{
    /// <summary>Spec §3: the repayment and its penalty have at most two decimals; the engine checks the rest.</summary>
    public static Failure? Rules(RepaymentDraft draft)
        => LoanRules.Amount(draft.Amount, "amount") ?? LoanRules.Amount(draft.Penalty, "penalty")
            ?? LoanRules.InYears(draft.OnDate, "onDate") ?? LoanRules.Note(draft.Note, "note");

    public static StoredRepayment Repayment(Guid id, RepaymentDraft draft, Guid? feeId)
        => new(id, new EarlyRepayment(draft.OnDate, draft.Amount, draft.Effect), feeId, draft.Note, 0);

    /// <summary>Spec §3: a penalty is a fee of kind <c>early_repayment_penalty</c> on its repayment's date (precision 9: no note).</summary>
    public static StoredFee? Penalty(StoredRepayment repayment, RepaymentDraft draft)
        => draft.Penalty is { } amount && repayment.FeeId is { } id
            ? new StoredFee(id, new LoanFee(FeeKind.EarlyRepaymentPenalty, amount, draft.OnDate), null, 0)
            : null;

    /// <summary>
    /// The loan rebuilt with <paramref name="written"/> in place of the repayment of its id (and that one's old penalty), and
    /// with <paramref name="penalty"/>; a refusal answers at the route's fields (precision 7). Null when it computes.
    /// </summary>
    public static Failure? Build(Currency currency, StoredLoan loan, StoredRepayment written, StoredFee? penalty)
    {
        var old = loan.Repayments.FirstOrDefault(r => r.Id == written.Id);
        var fees = loan.Fees.Where(f => f.Id != old?.FeeId).ToList();
        if (penalty is not null)
            fees.Add(penalty);
        var repayments = loan.Repayments.Where(r => r.Id != written.Id).Append(written)
            .OrderBy(r => r.Repayment.OnDate).ThenBy(r => r.Id).ToList();

        var built = LoanScheduleBuilder.Build(LoanReading.Terms(currency, loan.Contract, fees, repayments));
        return built.TryGetFailure(out var failure)
            ? LoanPaths.ForRepayment(failure!, repayments.FindIndex(r => r.Id == written.Id),
                penalty is null ? null : fees.FindIndex(f => f.Id == penalty.Id))
            : null;
    }

    /// <summary>A removal changes the calculation too (spec §1): an imposed payment may have relied on it (precision 7). Null when it computes.</summary>
    public static Failure? Removable(Currency currency, StoredLoan loan, StoredRepayment old)
    {
        var remaining = loan.Repayments.Where(r => r.Id != old.Id).ToList();
        var built = LoanScheduleBuilder.Build(LoanReading.Terms(currency, loan.Contract, loan.Fees.Where(f => f.Id != old.FeeId), remaining));
        if (!built.TryGetFailure(out var failure))
            return null;
        // Terms orders by date then id; the k-th remaining one is the stored list's index of that same repayment.
        var ordered = remaining.OrderBy(r => r.Repayment.OnDate).ThenBy(r => r.Id).ToList();
        var stored = loan.Repayments.OrderBy(r => r.Repayment.OnDate).ThenBy(r => r.Id).ToList();
        return LoanPaths.ForRemoval(failure!, k => stored.FindIndex(r => r.Id == ordered[k].Id));
    }

    /// <summary>The repayment to change: missing (404) or read since (409), in that order.</summary>
    public static (StoredRepayment? Old, Failure? Refused) Writable(StoredLoan loan, Guid id, uint version)
    {
        var old = loan.Repayments.FirstOrDefault(r => r.Id == id);
        if (old is null)
            return (null, new NotFoundFailure("loan repayment", id.ToString()));
        if (old.Version != version)
            return (old, LoanCheck.Conflict(LoanCodes.RepaymentConflict, "repayment"));
        return (old, null);
    }
}

/// <summary>Spec 3A2 §1 decision 4: the repayment and its penalty in one transaction.</summary>
[RequestHandler<AddLoanRepaymentCommand, Guid>]
public sealed class AddLoanRepaymentHandler(IItemRegistry items, ILoanStore loans, IUnitOfWork unitOfWork)
    : IRequestHandler<AddLoanRepaymentCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(AddLoanRepaymentCommand command, CancellationToken ct = default)
    {
        var draft = command.Repayment;
        if (RepaymentCheck.Rules(draft) is { } refused)
            return Result.Failure<Guid>(refused);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await LoanCheck.LockAsync(loans, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        loaded.TryGetValue(out var found);

        var written = RepaymentCheck.Repayment(Guid.CreateVersion7(), draft, draft.Penalty is null ? null : Guid.CreateVersion7());
        var penalty = RepaymentCheck.Penalty(written, draft);
        if (RepaymentCheck.Build(LoanViews.CurrencyOf(found.Item), found.Loan, written, penalty) is { } impossible)
            return Result.Failure<Guid>(impossible);

        if (penalty is not null)
            await loans.AddFeeAsync(command.ItemId, penalty.Id, penalty.Fee, null, ct);
        await loans.AddRepaymentAsync(command.ItemId, written.Id, written.Repayment, written.FeeId, written.Note, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(written.Id);
    }
}

[RequestHandler<EditLoanRepaymentCommand, Guid>]
public sealed class EditLoanRepaymentHandler(IItemRegistry items, ILoanStore loans, IUnitOfWork unitOfWork)
    : IRequestHandler<EditLoanRepaymentCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(EditLoanRepaymentCommand command, CancellationToken ct = default)
    {
        var draft = command.Values;
        if (RepaymentCheck.Rules(draft) is { } refused)
            return Result.Failure<Guid>(refused);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await LoanCheck.LockAsync(loans, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<Guid>(failure!);
        loaded.TryGetValue(out var found);
        var (old, unwritable) = RepaymentCheck.Writable(found.Loan, command.Repayment, command.Version);
        if (old is null || unwritable is not null)
            return Result.Failure<Guid>(unwritable!);

        // The penalty keeps its fee id while it exists, so its fee row is updated in place.
        var written = RepaymentCheck.Repayment(old.Id, draft, draft.Penalty is null ? null : old.FeeId ?? Guid.CreateVersion7());
        var penalty = RepaymentCheck.Penalty(written, draft);
        if (RepaymentCheck.Build(LoanViews.CurrencyOf(found.Item), found.Loan, written, penalty) is { } impossible)
            return Result.Failure<Guid>(impossible);

        await loans.UpdateRepaymentAsync(command.ItemId, written.Id, written.Repayment, written.FeeId, written.Note, ct);
        if (penalty is not null && old.FeeId is not null)
            await loans.UpdateFeeAsync(command.ItemId, penalty.Id, penalty.Fee, null, ct);
        else if (penalty is not null)
            await loans.AddFeeAsync(command.ItemId, penalty.Id, penalty.Fee, null, ct);
        else if (old.FeeId is { } removed)
            await loans.DeleteFeeAsync(command.ItemId, removed, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(written.Id);
    }
}

[RequestHandler<DeleteLoanRepaymentCommand, bool>]
public sealed class DeleteLoanRepaymentHandler(IItemRegistry items, ILoanStore loans, IUnitOfWork unitOfWork)
    : IRequestHandler<DeleteLoanRepaymentCommand, bool>
{
    public async ValueTask<Result<bool>> HandleAsync(DeleteLoanRepaymentCommand command, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await LoanCheck.LockAsync(loans, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<bool>(failure!);
        loaded.TryGetValue(out var found);
        var (old, unwritable) = RepaymentCheck.Writable(found.Loan, command.Repayment, command.Version);
        if (old is null || unwritable is not null)
            return Result.Failure<bool>(unwritable!);

        if (RepaymentCheck.Removable(LoanViews.CurrencyOf(found.Item), found.Loan, old) is { } impossible)
            return Result.Failure<bool>(impossible);
        var (id, penaltyId) = (old.Id, old.FeeId);

        await loans.DeleteRepaymentAsync(command.ItemId, id, ct);
        if (penaltyId is { } penalty)
            await loans.DeleteFeeAsync(command.ItemId, penalty, ct);
        await transaction.CommitAsync(ct);
        return Result.Success(true);
    }
}
