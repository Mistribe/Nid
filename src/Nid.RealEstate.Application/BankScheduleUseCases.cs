using System.Globalization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.RealEstate.Application;

/// <summary>Spec 3F2 §3: the pasted table read and compared with the calculation; nothing stored, read access only (as a simulation).</summary>
public sealed record PreviewBankScheduleQuery(ItemId ItemId, string Text) : IQuery<BankSchedulePreview>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

/// <summary>Spec 3F2 §3: the table replaced whole, all or nothing; the reference unchanged; <paramref name="Version"/> is the loan's as read.</summary>
public sealed record ReplaceBankScheduleCommand(ItemId ItemId, string Text, uint Version) : ICommand<ItemId>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>Spec 3F2 §3: the table removed; the calculation is the reference again.</summary>
public sealed record DeleteBankScheduleCommand(ItemId ItemId, uint Version) : ICommand<bool>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

internal static class BankCheck
{
    /// <summary>A table holds 1 200 rows at most (the engine's limit): far fewer characters than this.</summary>
    public const int TextMax = 200_000;

    /// <summary>
    /// The pasted text read, then checked whole against the loan (spec 3F2 §2–§3). A refusal names <c>rows[i].column</c>, i the
    /// line of the text (0-based, header and blank lines counted): every unreadable cell at once, else the first broken rule; an
    /// empty table, a text too long, or a stored repayment or imposed payment the table makes impossible answer at <c>text</c>.
    /// </summary>
    public static Result<IReadOnlyList<BankRow>> Read(string text, LoanTerms terms, LoanSchedule computed)
    {
        if (text.Length > TextMax)
            return Refuse(new Failure(LoanCodes.BankScheduleInvalid, $"A pasted table holds at most {TextMax} characters.", null), "text");
        var read = BankScheduleCsv.Read(text);
        if (read.Errors.Count > 0)
            return Result.Failure<IReadOnlyList<BankRow>>(FieldErrors.Many(LoanCodes.BankScheduleInvalid, "Some lines of the table cannot be read.",
                read.Errors.Select(e => new FieldError(e.Column.Length == 0 ? $"rows[{e.Line}]" : $"rows[{e.Line}].{e.Column}",
                    LoanCodes.BankScheduleInvalid, e.Message)).ToList()));
        var rows = read.Rows.Select(r => r.Row).ToList();
        if (!BankSchedule.Merge(terms, computed, rows).TryGetFailure(out var refused))
            return Result.Success<IReadOnlyList<BankRow>>(rows);
        var field = LoanPaths.FieldOf(refused!) ?? "";
        return Refuse(refused!, field.StartsWith("rows[", StringComparison.Ordinal) ? Relocated(field, read.Rows) : "text");
    }

    /// <summary><c>rows[k].column</c>, k the rank among the rows read, as <c>rows[line].column</c>.</summary>
    private static string Relocated(string field, IReadOnlyList<BankCsvRow> rows)
    {
        var close = field.IndexOf(']');
        var k = int.Parse(field["rows[".Length..close], CultureInfo.InvariantCulture);
        return $"rows[{rows[k].Line}]{field[(close + 1)..]}";
    }

    private static Result<IReadOnlyList<BankRow>> Refuse(Failure failure, string path)
        => Result.Failure<IReadOnlyList<BankRow>>(FieldErrors.At(failure, path));
}

[RequestHandler<PreviewBankScheduleQuery, BankSchedulePreview>]
public sealed class PreviewBankScheduleHandler(ILoanStore loans, IItemRegistry items) : IRequestHandler<PreviewBankScheduleQuery, BankSchedulePreview>
{
    public async ValueTask<Result<BankSchedulePreview>> HandleAsync(PreviewBankScheduleQuery query, CancellationToken ct = default)
    {
        var loaded = await LoanCheck.LoadAsync(loans, items, query.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<BankSchedulePreview>(failure!);
        loaded.TryGetValue(out var found);
        var terms = LoanReading.Terms(LoanViews.CurrencyOf(found.Item), found.Loan.Contract, found.Loan.Fees, found.Loan.Repayments);
        var computed = LoanReading.Computed(terms);
        var read = BankCheck.Read(query.Text, terms, computed);
        if (read.TryGetFailure(out failure))
            return Result.Failure<BankSchedulePreview>(failure!);
        read.TryGetValue(out var rows);
        return Result.Success(new BankSchedulePreview(rows!.Select(LoanViews.BankRowOf).ToList(),
            LoanViews.ComparisonOf(BankSchedule.Diff(computed, rows!).Summary)));
    }
}

/// <summary>Spec 3F2 §3: locked, versioned as the contract; the reference is left as it is.</summary>
[RequestHandler<ReplaceBankScheduleCommand, ItemId>]
public sealed class ReplaceBankScheduleHandler(IItemRegistry items, ILoanStore loans, IUnitOfWork unitOfWork)
    : IRequestHandler<ReplaceBankScheduleCommand, ItemId>
{
    public async ValueTask<Result<ItemId>> HandleAsync(ReplaceBankScheduleCommand command, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await LoanCheck.LockAsync(loans, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<ItemId>(failure!);
        loaded.TryGetValue(out var found);
        if (found.Loan.Version != command.Version)
            return Result.Failure<ItemId>(LoanCheck.Conflict(LoanCodes.LoanConflict, "loan"));

        var terms = LoanReading.Terms(LoanViews.CurrencyOf(found.Item), found.Loan.Contract, found.Loan.Fees, found.Loan.Repayments);
        var read = BankCheck.Read(command.Text, terms, LoanReading.Computed(terms));
        if (read.TryGetFailure(out failure))
            return Result.Failure<ItemId>(failure!);
        read.TryGetValue(out var rows);
        // Spec 3F2 precision 5, as a phase's (3F1 §2): a row keeps the file id of the stored row of its date, so the file the table
        // came from re-imports — unchanged, and the app's row stays.
        var marks = found.Loan.Bank.Where(b => b.Import is not null).ToDictionary(b => b.Row.DueDate, b => b.Import);
        await loans.ReplaceBankScheduleAsync(command.ItemId,
            rows!.Select(r => new StoredBankRow(Guid.CreateVersion7(), r) { Import = marks.GetValueOrDefault(r.DueDate) }).ToList(), ct);
        await transaction.CommitAsync(ct);
        return Result.Success(command.ItemId);
    }
}

[RequestHandler<DeleteBankScheduleCommand, bool>]
public sealed class DeleteBankScheduleHandler(IItemRegistry items, ILoanStore loans, IUnitOfWork unitOfWork)
    : IRequestHandler<DeleteBankScheduleCommand, bool>
{
    public async ValueTask<Result<bool>> HandleAsync(DeleteBankScheduleCommand command, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await LoanCheck.LockAsync(loans, items, command.ItemId, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<bool>(failure!);
        loaded.TryGetValue(out var found);
        if (found.Loan.Version != command.Version)
            return Result.Failure<bool>(LoanCheck.Conflict(LoanCodes.LoanConflict, "loan"));

        await loans.ReplaceBankScheduleAsync(command.ItemId, [], ct); // the calculation is the reference again
        await transaction.CommitAsync(ct);
        return Result.Success(true);
    }
}
