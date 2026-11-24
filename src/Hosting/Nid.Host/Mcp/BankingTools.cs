using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nid.Banking.Application;
using Nid.Banking.Application.Spending;
using Nid.Core.Contracts;
using Nid.Host.Api;
using NodaTime;
using UnambitiousFx.Synapse.Abstractions;

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
            ? McpCalls.RunAsync(services, r => new ListBankTransactionsQuery(new ItemId(envelope), r.OptionalDate(from, "from"), r.OptionalDate(to, "to"), page, state), ct)
            : McpCalls.RunAsync(services, r => new ListBankLinesQuery(state, r.OptionalDate(from, "from"), r.OptionalDate(to, "to"), page), ct);

    /// <summary>One line to categorise: what the person sees on the statement, and the id to propose a category for.</summary>
    public sealed record UncategorisedLine(Guid Id, LocalDate BookedOn, string Label, string? Counterparty, string Amount, string Currency, string AccountName);

    /// <summary>A category a line may take: <paramref name="Kind"/> is expense, income or transfer.</summary>
    public sealed record CategoryChoice(Guid Id, string Name, string? ParentName, string Kind);

    public sealed record UncategorisedLines(IReadOnlyList<UncategorisedLine> Items, bool HasMore, IReadOnlyList<CategoryChoice> Categories);

    [McpServerTool(Name = "list_uncategorised_bank_transactions", ReadOnly = true)]
    [Description("The booked bank lines of the accounts the person reads that have no category yet (ignored lines left out), newest first, 50 per page, "
        + "with the categories in use (id, name, parent, kind: expense, income or transfer). Pick a category for each line and send them to "
        + "propose_bank_categories. A line already in a pending proposal may be listed again (a second approval skips it). " + McpSetup.Untrusted)]
    public static async Task<CallToolResult> ListUncategorisedBankTransactions(IServiceProvider services,
        [Description("Page, from 1.")] int page = 1, CancellationToken ct = default)
    {
        var listed = await services.GetRequiredService<IInvoker>().InvokeAsync(new ListBankCategoriesQuery(), ct);
        if (listed.TryGetFailure(out var failure))
            return McpCalls.Refuse(services, failure!);
        var categories = listed.TryGetValue(out var all) ? all! : [];
        var names = categories.ToDictionary(c => c.Id, c => c.Name);
        return await McpCalls.RunAsync(services, () => new ListBankLinesQuery("all", null, null, page, Uncategorised: true), (BankTransactionPage lines) =>
        {
            return new UncategorisedLines(
                [.. lines.Items.Select(t => new UncategorisedLine(t.Id, t.BookedOn, t.Label, t.Counterparty, t.Amount, t.Currency, t.AccountName))], lines.HasMore,
                [.. categories.Where(c => !c.Archived).Select(c => new CategoryChoice(c.Id, c.Name, c.ParentId is { } p ? names.GetValueOrDefault(p) : null, c.Kind))]);
        }, ct);
    }

    [McpServerTool(Name = "get_spending", ReadOnly = true)]
    [Description("What the household's bank accounts spent by category over one month (default: the current month) or over a period of "
        + "whole months ('from' and 'to' both given, at most 24 months; not with 'month'), with each category's "
        + "monthly average over the 12 months before it, or over the months the accounts' history covers when fewer (none under 3). Amounts "
        + "are in the household's reference currency; transfers between accounts never count, refunds lower their category, and lines with "
        + "no category are counted apart as 'uncategorised'. "
        + "'incomplete' means a bank line in another currency had no exchange rate and is left out of the figures; 'budgetIncomplete' means "
        + "an expected charge (a commitment) in another currency had no rate and is left out of the budgets. "
        + "In the household scope and over one month only, a category may carry its monthly 'budget': 'planned' (the typed amount, a yearly one divided by 12, "
        + "or with source 'committed' the month's expected charges there such as subscriptions and loan instalments), 'committed' (those "
        + "charges, also inside a typed amount), 'left' (planned minus spent, negative once over), 'expectedSoFar' for the current month "
        + "(the charges already due, plus the rest of planned spread over the days elapsed) and 'state' ('on_track', 'ahead' of that pace, 'over'). "
        + "The month's budget, as the home page shows it: 'income' (declared income, monthly equivalent; null when none is declared), "
        + "'committed' (subscriptions, loans, childcare, property charges and tax, per month), 'everyday' (the month's spending less the lines "
        + "linked to a commitment and the tax), 'left' (income minus committed minus everyday, negative once overspent) and, for the current "
        + "month only, 'leftEstimate': what would be left if the month ends as usual (income minus committed minus the larger of everyday and "
        + "the usual month's everyday spending); null when it cannot be computed. Over a period, 'income', 'committed', 'everyday' and 'left' "
        + "are the sums of its months (each null when one month's is) and there is no 'leftEstimate'. 'savingsRate' is left divided by income, "
        + "in percent, for the month or the whole period (null without an income above 0). 'months' lists every month from 'from' to 'to', "
        + "oldest first (a single entry for one month), each with what it spent ('total') and its own 'income', 'committed', 'everyday' and 'left'. "
        + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetSpending(IServiceProvider services,
        [Description("ISO month (yyyy-mm); default the current month.")] string? month = null,
        [Description("'household' (default) or 'me'.")] string? scope = null,
        [Description("ISO first month of a period (yyyy-mm), with 'to'.")] string? from = null,
        [Description("ISO last month of a period (yyyy-mm), with 'from'.")] string? to = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, r => new GetSpendingQuery(r.Scope(scope), r.Month(month, "month"),
            r.Month(from, "from"), r.Month(to, "to")), ct);

    [McpServerTool(Name = "list_expected_charges", ReadOnly = true)]
    [Description("The charges expected between two dates (subscriptions, loan instalments, rents…) that the person may link a bank line to, "
        + "at most 92 days. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> ListExpectedCharges(IServiceProvider services,
        [Description("ISO first date (yyyy-mm-dd).")] string from, [Description("ISO last date (yyyy-mm-dd).")] string to, CancellationToken ct = default)
        => McpCalls.RunAsync(services, r => new ListExpectedChargesQuery(r.RequiredDate(from, "from"), r.RequiredDate(to, "to")), ct);
}
