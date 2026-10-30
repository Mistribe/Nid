using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nid.Banking.Application;
using Nid.Core.Contracts;
using Nid.Host.Api;

namespace Nid.Host.Mcp;

/// <summary>5C spec §8: the bank lines of the accounts the person reads, with their links, and the charges a line may pay. Amounts are decimal strings.</summary>
[McpServerToolType]
public sealed class BankingTools
{
    [McpServerTool(Name = "list_bank_transactions", ReadOnly = true)]
    [Description("The booked bank lines of the accounts the person reads, newest first, 50 per page, each with its link (the charge it pays) when the "
        + "person may see it. By default only the lines not linked yet. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> ListBankTransactions(IServiceProvider services,
        [Description("Only the lines of the account associated with this envelope (list_envelopes).")] Guid? envelopeId = null,
        [Description("ISO first booking date (yyyy-mm-dd).")] string? from = null, [Description("ISO last booking date (yyyy-mm-dd).")] string? to = null,
        [Description("'unlinked' (default), 'linked', 'ignored', 'internal' or 'all'.")] string state = "unlinked",
        [Description("Page, from 1.")] int page = 1, CancellationToken ct = default)
        => envelopeId is { } envelope
            ? McpCalls.RunAsync(services, () => new ListBankTransactionsQuery(new ItemId(envelope), SubscriptionEndpoints.OptionalDate(from), SubscriptionEndpoints.OptionalDate(to), page, state), ct)
            : McpCalls.RunAsync(services, () => new ListBankLinesQuery(state, SubscriptionEndpoints.OptionalDate(from), SubscriptionEndpoints.OptionalDate(to), page), ct);

    [McpServerTool(Name = "list_expected_charges", ReadOnly = true)]
    [Description("The charges expected between two dates (subscriptions, loan instalments, rents…) that the person may link a bank line to, "
        + "at most 92 days. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> ListExpectedCharges(IServiceProvider services,
        [Description("ISO first date (yyyy-mm-dd).")] string from, [Description("ISO last date (yyyy-mm-dd).")] string to, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new ListExpectedChargesQuery(SubscriptionEndpoints.RequireDate(from), SubscriptionEndpoints.RequireDate(to)), ct);
}
