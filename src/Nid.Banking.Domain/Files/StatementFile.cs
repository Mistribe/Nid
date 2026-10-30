using NodaTime;

namespace Nid.Banking.Domain.Files;

/// <summary>A booked line of a statement file: the amount is signed (debit negative), the label never empty.</summary>
public sealed record StatementLine(string? ProviderRef, LocalDate BookedOn, LocalDate? ValueOn, decimal Amount, string Label, string? Counterparty, string? Remittance);

/// <summary>
/// Spec §5: one account's statement read from an OFX or CAMT.053 file (<see cref="Format"/> is <c>ofx</c> or <c>camt053</c>);
/// <see cref="Card"/> for an OFX credit card statement (<c>CCSTMTRS</c>).
/// </summary>
public sealed record StatementFile(
    string Format, string? Iban, string AccountId, string Currency,
    decimal? ClosingBalance, LocalDate? ClosingBalanceOn, LocalDate? From, LocalDate? To, IReadOnlyList<StatementLine> Lines, bool Card = false);

/// <summary>A file refused whole: <see cref="Line"/> is the file's line at fault, when known.</summary>
public sealed class StatementFileException(string code, int? line, string message) : Exception(message)
{
    public string Code => code;
    public int? Line => line;
}
