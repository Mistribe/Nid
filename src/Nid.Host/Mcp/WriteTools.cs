using System.ComponentModel;
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

/// <summary>Spec 4D1 D1: one tool per document; a proposal by default, a direct write with `apply` and `write:direct` (D2).</summary>
[McpServerToolType]
public sealed class WriteTools
{
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
        => McpCalls.ProposeAsync(services, "investments.record_statement", envelopeId, new { asOf, value, movements }, sourceLabel, apply, ct);
}
