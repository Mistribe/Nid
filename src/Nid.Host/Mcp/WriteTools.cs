using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Nid.Host.Mcp;

/// <summary>An amount as the document prints it, copied as a decimal string.</summary>
public sealed record MoneyArgument(
    [property: Description("A decimal string with a point, e.g. '1234.56'.")] string? Amount,
    [property: Description("ISO 4217 code, e.g. 'EUR'.")] string? Currency);

/// <summary>One line of a statement, as printed.</summary>
public sealed record StatementMovement(
    [property: Description("The line's date, ISO (yyyy-mm-dd).")] string? On,
    [property: Description("'contribution', 'withdrawal', 'income' or 'expense'.")] string? Kind,
    [property: Description("A positive decimal string with a point, in the envelope's currency.")] string? Amount,
    [property: Description("The line's text as printed, 200 characters at most.")] string? Label,
    [property: Description("The bank's reference for the line, if printed, 100 characters at most.")] string? Ref);

/// <summary>One line of a holdings statement, as printed (issue #34 §6).</summary>
public sealed record HoldingsLine(
    [property: Description("The holding's name as printed, e.g. 'R-Co Valor C EUR'.")] string? Name,
    [property: Description("The ISIN, if printed (12 characters).")] string? Isin,
    [property: Description("Only for a holding Nid does not have yet: 'euro_fund', 'fund', 'etf', 'equity', 'bond', 'structured', 'cash' or 'other'.")] string? Kind,
    [property: Description("The units, if printed, a decimal string with a point; none for a euro fund or cash.")] string? Units,
    [property: Description("The unit price, if printed, a decimal string with a point; none for a euro fund or cash.")] string? UnitPrice,
    [property: Description("The line's value as printed, a decimal string with a point, in the envelope's currency.")] string? Value,
    [property: Description("The average cost per unit (PRU), if printed.")] string? CostBasisPerUnit);

/// <summary>One movement on a holding, as printed.</summary>
public sealed record HoldingsMovement(
    [property: Description("The holding's ISIN or name, as a line or the envelope names it.")] string? Holding,
    [property: Description("The value date, ISO (yyyy-mm-dd).")] string? On,
    [property: Description("'allocation_in' (a payment's share), 'allocation_out' (a withdrawal's share), 'maturity', 'fee' (taken in units), "
        + "'interest' (profit sharing) or 'income' (a dividend). Switches are left out.")] string? Kind,
    [property: Description("The units, if printed, a decimal string with a point; Nid gives them the kind's sign.")] string? Units,
    [property: Description("The unit price, if printed.")] string? UnitPrice,
    [property: Description("A positive decimal string with a point, in the envelope's currency.")] string? Amount,
    [property: Description("The line's text as printed, 200 characters at most.")] string? Label,
    [property: Description("The insurer's reference for the line, if printed, 200 characters at most.")] string? Ref);

/// <summary>One row of a bank's amortisation table, as printed (spec 4D2 §2, 3F2's <c>BankRow</c>).</summary>
public sealed record AmortizationRow(
    [property: Description("The due date, ISO (yyyy-mm-dd).")] string? DueDate,
    [property: Description("The payment without insurance, a decimal string with a point, e.g. '1159.92'.")] string? Payment,
    [property: Description("The interest, a decimal string with a point.")] string? Interest,
    [property: Description("The principal repaid, a decimal string with a point.")] string? Principal,
    [property: Description("The insurance, only if the table gives it, a decimal string with a point.")] string? Insurance,
    [property: Description("The remaining balance right after the payment (capital restant dû), a decimal string with a point.")] string? Crd);

/// <summary>Spec 4D1 D1: one tool per document; a proposal by default, a direct write with `apply` and `write:direct` (D2).</summary>
[McpServerToolType]
public sealed partial class WriteTools
{
    private sealed record StatementArguments(string AsOf, MoneyArgument? Value, StatementMovement[]? Movements);

    private sealed record HoldingsSnapshotArguments(
        string AsOf, string? SourceLabel, HoldingsLine[] Lines, HoldingsMovement[]? Movements, bool RecordValuation);

    private sealed record PayslipArguments(string PaidOn, MoneyArgument Net, MoneyArgument? Gross, MoneyArgument? Taxable, string? PhaseLabel);

    private sealed record InvoiceArguments(string ChargedOn, MoneyArgument Amount);

    private sealed record AmortizationArguments(AmortizationRow[] Rows);

    /// <summary>A tool's arguments as a proposal payload: camelCase, absent fields left out (the handler normalizes the rest).</summary>
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(StatementArguments))]
    [JsonSerializable(typeof(HoldingsSnapshotArguments))]
    [JsonSerializable(typeof(PayslipArguments))]
    [JsonSerializable(typeof(InvoiceArguments))]
    [JsonSerializable(typeof(AmortizationArguments))]
    private sealed partial class ArgumentsJson : JsonSerializerContext;

    /// <summary>The tools' own parameter types, for the SDK's options (McpSetup).</summary>
    internal static JsonSerializerContext Arguments => ArgumentsJson.Default;

    /// <summary>What every write tool's description ends with (spec 4D1 §5): no « Untrusted » sentence, a rule on copying.</summary>
    public const string Rules = "Copy the document as written and invent nothing: leave out what it does not show. "
        + "By default this creates a proposal that an adult approves in Nid; apply=true writes directly and needs the 'write:direct' scope.";

    [McpServerTool(Name = "propose_statement", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("An account statement for one envelope: its value on the statement's date and the movements it lists. One call per "
        + "envelope; the envelopes of one statement share a sourceLabel. Movements already in Nid are flagged and left out. " + Rules)]
    public static Task<CallToolResult> ProposeStatement(IServiceProvider services,
        [Description("The envelope's id (list_envelopes).")] Guid envelopeId,
        [Description("The statement's date, ISO (yyyy-mm-dd).")] string asOf,
        [Description("The envelope's value on asOf, in its currency.")] MoneyArgument? value = null,
        [Description("The statement's movements for this envelope, at most 200.")] StatementMovement[]? movements = null,
        [Description("The document's name, e.g. 'Relevé PEA septembre 2025', 200 characters at most.")] string? sourceLabel = null,
        [Description("true: write directly instead of proposing.")] bool apply = false,
        CancellationToken ct = default)
        => McpCalls.ProposeAsync(services, "investments.record_statement", envelopeId, JsonSerializer.SerializeToElement(new StatementArguments(asOf, value, movements), ArgumentsJson.Default.StatementArguments), sourceLabel, apply, ct);

    [McpServerTool(Name = "propose_holdings_snapshot", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("A statement's holdings for one envelope: each holding's line on the statement's date and the movements it lists per "
        + "holding. One call per envelope and statement. Holdings are matched by ISIN, then by exact name; any other line is proposed as a "
        + "new holding. A snapshot of the same date is replaced. Movements already in Nid are flagged and left out. " + Rules)]
    public static Task<CallToolResult> ProposeHoldingsSnapshot(IServiceProvider services,
        [Description("The envelope's id (list_envelopes).")] Guid envelopeId,
        [Description("The statement's date, ISO (yyyy-mm-dd).")] string asOf,
        [Description("Every holding line of the statement, at most 100.")] HoldingsLine[] lines,
        [Description("The statement's movements per holding, at most 200.")] HoldingsMovement[]? movements = null,
        [Description("true: also record the lines' sum as the envelope's value on asOf.")] bool recordValuation = false,
        [Description("The document's name, e.g. 'Information trimestrielle 30/06/2026', 200 characters at most.")] string? sourceLabel = null,
        [Description("true: write directly instead of proposing.")] bool apply = false,
        CancellationToken ct = default)
        => McpCalls.ProposeAsync(services, "investments.record_holdings_snapshot", envelopeId,
            JsonSerializer.SerializeToElement(new HoldingsSnapshotArguments(asOf, sourceLabel, lines, movements, recordValuation), ArgumentsJson.Default.HoldingsSnapshotArguments), sourceLabel, apply, ct);

    [McpServerTool(Name = "propose_payslip", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("A payslip for one income source. Nid compares its net with the expected pay: within the tolerance (2 % by default), "
        + "with the same gross when both are known, and with the same net taxable income when the payslip gives one, it answers 'conforme' and records nothing; otherwise the expected pay "
        + "changes from that month's payment date. A payslip paid when no period of the source runs (before its first period, or "
        + "after it ended) is refused at paidOn: Nid never creates a period, propose_add_income_phase adds one first. " + Rules)]
    public static Task<CallToolResult> ProposePayslip(IServiceProvider services,
        [Description("The income source's id (list_income_sources).")] Guid incomeSourceId,
        [Description("The payment date printed on the payslip, ISO (yyyy-mm-dd).")] string paidOn,
        [Description("The net paid.")] MoneyArgument net,
        [Description("The gross, if printed.")] MoneyArgument? gross = null,
        [Description("The net taxable income (« net imposable »), if printed.")] MoneyArgument? taxable = null,
        [Description("The period's label (get_income_source), needed when several run at once.")] string? phaseLabel = null,
        [Description("The document's name, e.g. 'Bulletin de paie septembre 2025', 200 characters at most.")] string? sourceLabel = null,
        [Description("true: write directly instead of proposing.")] bool apply = false,
        CancellationToken ct = default)
        => McpCalls.ProposeAsync(services, "income.change_pay", incomeSourceId, JsonSerializer.SerializeToElement(new PayslipArguments(paidOn, net, gross, taxable, phaseLabel), ArgumentsJson.Default.PayslipArguments), sourceLabel, apply, ct);

    [McpServerTool(Name = "propose_invoice", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("An invoice for one subscription, all taxes included. Nid compares it with the price on that date: the same, to the "
        + "cent, answers 'conforme' and records nothing; otherwise the price changes from chargedOn. " + Rules)]
    public static Task<CallToolResult> ProposeInvoice(IServiceProvider services,
        [Description("The subscription's id (list_subscriptions).")] Guid subscriptionId,
        [Description("The date charged, ISO (yyyy-mm-dd).")] string chargedOn,
        [Description("The amount charged, all taxes included.")] MoneyArgument amount,
        [Description("The document's name, e.g. 'Facture Netflix septembre 2025', 200 characters at most.")] string? sourceLabel = null,
        [Description("true: write directly instead of proposing.")] bool apply = false,
        CancellationToken ct = default)
        => McpCalls.ProposeAsync(services, "subscriptions.change_price", subscriptionId, JsonSerializer.SerializeToElement(new InvoiceArguments(chargedOn, amount), ArgumentsJson.Default.InvoiceArguments), sourceLabel, apply, ct);

    [McpServerTool(Name = "propose_amortization", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("A bank's amortisation table for one loan: every row as printed, in date order, 1200 rows at most, the whole table in "
        + "one call. Nid checks it against the loan and compares it with its own schedule; the answer's summary gives the largest gap "
        + "and the date the remaining balance diverges by more than 1, to tell the adult. The same table already the loan's reference "
        + "answers 'conforme'. Approved, it replaces the stored table and, unless the adult says otherwise, becomes the loan's "
        + "reference; applied directly, it becomes the reference. " + Rules)]
    public static Task<CallToolResult> ProposeAmortization(IServiceProvider services,
        [Description("The loan's id (list_loans).")] Guid loanId,
        [Description("Every row of the table, as printed: payment excludes insurance; crd is the balance after the payment.")] AmortizationRow[] rows,
        [Description("The document's name, e.g. 'Tableau d’amortissement prêt maison', 200 characters at most.")] string? sourceLabel = null,
        [Description("true: write directly instead of proposing.")] bool apply = false,
        CancellationToken ct = default)
        => McpCalls.ProposeAsync(services, "realestate.import_amortization", loanId, JsonSerializer.SerializeToElement(new AmortizationArguments(rows), ArgumentsJson.Default.AmortizationArguments), sourceLabel, apply, ct);
}
