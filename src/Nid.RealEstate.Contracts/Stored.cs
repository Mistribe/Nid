using System.Text;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.RealEstate.Contracts;

/// <summary>
/// Spec 3A2 §2: the text of the loan enums in columns and on the wire, snake_case as loans.md writes them
/// (<c>half_up</c>, <c>prorata_actual_365</c>). One rule for every enum, as <c>IncomeWire</c> is Income's table.
/// </summary>
public static class LoanWire
{
    public static string Of<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        var text = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && (char.IsUpper(name[i]) || (char.IsDigit(name[i]) && !char.IsDigit(name[i - 1]))))
                text.Append('_');
            text.Append(char.ToLowerInvariant(name[i]));
        }
        return text.ToString();
    }

    public static bool TryParse<T>(string? wire, out T value) where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
            if (Of(candidate) == wire)
            {
                value = candidate;
                return true;
            }
        value = default;
        return false;
    }
}

/// <summary>
/// A phase as stored: the engine's phase and its note. <c>Import</c> is the file id it was imported under (spec 3F1 §2), null when
/// made in the app; <c>Id</c> is its row's, set when read back — a contract write gives its rows new ids, not new marks.
/// </summary>
public sealed record LoanPhaseEntry(LoanPhase Phase, string? Note)
{
    public ImportMark? Import { get; init; }

    public Guid Id { get; init; }
}

/// <summary>An insurance as stored: the engine's insurance and its note; <c>Import</c> and <c>Id</c> as a phase's.</summary>
public sealed record LoanInsuranceEntry(LoanInsurance Insurance, string? Note)
{
    public ImportMark? Import { get; init; }

    public Guid Id { get; init; }
}

/// <summary>Spec 3A2 §1: what one <c>PUT</c> replaces whole — the terms, the phases (read back by <c>Seq</c>), the insurances.</summary>
public sealed record LoanContract(
    decimal Principal, LocalDate DisbursedOn, LocalDate FirstPaymentDate, int TotalPayments, InterestRounding Rounding,
    FirstPeriod FirstPeriod, IReadOnlyList<LoanPhaseEntry> Phases, IReadOnlyList<LoanInsuranceEntry> Insurances);

public sealed record StoredFee(Guid Id, LoanFee Fee, string? Note, uint Version);

/// <summary>An early repayment; <c>FeeId</c> is its penalty, a fee of kind <c>early_repayment_penalty</c> on its date.</summary>
public sealed record StoredRepayment(Guid Id, EarlyRepayment Repayment, Guid? FeeId, string? Note, uint Version);

/// <summary>
/// A loan as stored. <c>Version</c> is the loan row's, the contract's; fees and repayments by date, then id;
/// <c>PropertyId</c> is the property it finances (spec 3C1 §2), a column of the same row.
/// </summary>
public sealed record StoredLoan(
    ItemId ItemId, string? Lender, string? Note, LoanContract Contract, uint Version,
    IReadOnlyList<StoredFee> Fees, IReadOnlyList<StoredRepayment> Repayments, ItemId? PropertyId = null);

/// <summary>A change: a null field is left as it is; <c>Clear*</c> empties an optional one. <c>PropertyId</c> links the loan (spec 3C1 §1.5).</summary>
public sealed record LoanDetailsChange(
    string? Lender = null, bool ClearLender = false, string? Note = null, bool ClearNote = false,
    ItemId? PropertyId = null, bool ClearProperty = false);

public interface ILoanStore
{
    Task CreateAsync(ItemId item, string? lender, string? note, ItemId? property, LoanContract contract, CancellationToken ct);

    /// <summary>Null when the item is not a loan (or does not exist).</summary>
    Task<StoredLoan?> FindAsync(ItemId item, CancellationToken ct);

    Task<IReadOnlyList<StoredLoan>> FindManyAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct);

    /// <summary>The loans linked to these properties, whoever may read them: the caller filters (spec 3C1 §1.7).</summary>
    Task<IReadOnlyList<StoredLoan>> FindLinkedAsync(IReadOnlyCollection<ItemId> properties, CancellationToken ct);

    /// <summary>Locks the loan's row until the transaction ends: a loan's writes run one after the other (precision 5).</summary>
    Task LockAsync(ItemId item, CancellationToken ct);

    Task UpdateDetailsAsync(ItemId item, LoanDetailsChange change, CancellationToken ct);

    /// <summary>The terms in place, the phases and insurances deleted and written again; the loan's version always moves.</summary>
    Task ReplaceContractAsync(ItemId item, LoanContract contract, CancellationToken ct);

    Task AddFeeAsync(ItemId loan, Guid id, LoanFee fee, string? note, CancellationToken ct);

    /// <summary>Every value in place; the fee's version always moves.</summary>
    Task UpdateFeeAsync(ItemId loan, Guid id, LoanFee fee, string? note, CancellationToken ct);

    Task DeleteFeeAsync(ItemId loan, Guid id, CancellationToken ct);

    Task AddRepaymentAsync(ItemId loan, Guid id, EarlyRepayment repayment, Guid? feeId, string? note, CancellationToken ct);

    /// <summary>Every value in place; the repayment's version always moves.</summary>
    Task UpdateRepaymentAsync(ItemId loan, Guid id, EarlyRepayment repayment, Guid? feeId, string? note, CancellationToken ct);

    Task DeleteRepaymentAsync(ItemId loan, Guid id, CancellationToken ct);
}
