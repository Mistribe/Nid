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
            ? McpCalls.RunAsync(services, () => new ListBankTransactionsQuery(new ItemId(envelope), SubscriptionEndpoints.OptionalDate(from), SubscriptionEndpoints.OptionalDate(to), page, state), ct)
            : McpCalls.RunAsync(services, () => new ListBankLinesQuery(state, SubscriptionEndpoints.OptionalDate(from), SubscriptionEndpoints.OptionalDate(to), page), ct);

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
    [Description("What the household's bank accounts spent by category over one month (default: the current month), with each category's "
        + "monthly average over the 12 months before it, or over the months the accounts' history covers when fewer (none under 3). Amounts "
        + "are in the household's reference currency; transfers between accounts never count, refunds lower their category, and lines with "
        + "no category are counted apart as 'uncategorised'. "
        + "'incomplete' means a bank line in another currency had no exchange rate and is left out of the figures; 'budgetIncomplete' means "
        + "an expected charge (a commitment) in another currency had no rate and is left out of the budgets. "
        + "In the household scope only, a category may carry its monthly 'budget': 'planned' (the typed amount, a yearly one divided by 12, "
        + "or with source 'committed' the month's expected charges there such as subscriptions and loan instalments), 'committed' (those "
        + "charges, also inside a typed amount), 'left' (planned minus spent, negative once over), 'expectedSoFar' for the current month "
        + "(the charges already due, plus the rest of planned spread over the days elapsed) and 'state' ('on_track', 'ahead' of that pace, 'over'). "
        + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetSpending(IServiceProvider services,
        [Description("ISO month (yyyy-mm); default the current month.")] string? month = null,
        [Description("'household' (default) or 'me'.")] string? scope = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetSpendingQuery(SubscriptionEndpoints.ParseScope(scope), SubscriptionEndpoints.OptionalMonth(month), null, null), ct);

    [McpServerTool(Name = "list_expected_charges", ReadOnly = true)]
    [Description("The charges expected between two dates (subscriptions, loan instalments, rents…) that the person may link a bank line to, "
        + "at most 92 days. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> ListExpectedCharges(IServiceProvider services,
        [Description("ISO first date (yyyy-mm-dd).")] string from, [Description("ISO last date (yyyy-mm-dd).")] string to, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new ListExpectedChargesQuery(SubscriptionEndpoints.RequireDate(from), SubscriptionEndpoints.RequireDate(to)), ct);
}
