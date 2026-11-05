using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nid.Core.Application.Tax;
using Nid.Core.Contracts;
using Nid.Host.Api;

namespace Nid.Host.Mcp;

[McpServerToolType]
public sealed class TaxTools
{
    /// <summary>Issue #284: the same view as <c>GET /api/v1/tax</c>.</summary>
    [McpServerTool(Name = "get_tax", ReadOnly = true)]
    [Description("The tax page of a revenue year: the estimate and its detail, the tax profile the household declared (profile; profileFrom: " +
                 "the earlier year whose profile stands in), the withholding figures and the suggested cut (result.withholding), and what the estimate left out (notices, gaps). " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetTax(IServiceProvider services,
        [Description("The revenue year (default: this year).")] int? year = null,
        [Description("'household' (default) or 'me'.")] string? scope = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetTaxEstimateQuery(year, SubscriptionEndpoints.ParseScope(scope)), ct);

    /// <summary>Issue #410: the same view as <c>GET /api/v1/tax/changes</c>.</summary>
    [McpServerTool(Name = "get_tax_changes", ReadOnly = true)]
    [Description("What changed in the income tax since the year before, line by line (salaries, rental income and deficit, PER deduction, " +
                 "shares, decote, reductions, credits, flat tax): last year's value (the notice's when fromNotice), this year's, and the indicative " +
                 "effect of the change on this year's tax, sorted by effect; remainder is what the lines do not explain. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetTaxChanges(IServiceProvider services,
        [Description("The revenue year compared with the year before (default: this year).")] int? year = null,
        [Description("'household' (default) or 'me'.")] string? scope = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetTaxChangesQuery(year, SubscriptionEndpoints.ParseScope(scope)), ct);

    /// <summary>Rental 2044 spec §7: the same view as <c>GET /api/v1/tax/2044</c>.</summary>
    [McpServerTool(Name = "get_rental_2044", ReadOnly = true)]
    [Description("The 2044 rental income form of a revenue year: each rented property's lines (211 to 263, whole euros), the household's " +
                 "result and deficit split, the 2042 boxes to copy (4BA, 4BB, 4BC; 4BE under the micro-foncier) and the expenses to check. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetRental2044(IServiceProvider services,
        [Description("The revenue year (default: last year).")] int? year = null,
        [Description("'household' (default) or 'me'.")] string? scope = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetRental2044Query(year, SubscriptionEndpoints.ParseScope(scope)), ct);
    /// <summary>Sale tax spec §6: the same view as <c>GET /api/v1/tax/sale</c>.</summary>
    [McpServerTool(Name = "get_sale_tax_estimate", ReadOnly = true)]
    [Description("The tax a sale of everything held today would cost: each property's and each open PEA, life insurance or brokerage " +
                 "account's gain, income tax, social levies, property surtax and net proceeds, and the household total. Savings and checking " +
                 "accounts are listed as not taxable, a PER as not estimated. With 'item', that one item's page. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetSaleTaxEstimate(IServiceProvider services,
        [Description("'household' (default) or 'me'.")] string? scope = null,
        [Description("One property or envelope's id, for its page instead of the total.")] Guid? item = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetSaleTaxQuery(SubscriptionEndpoints.ParseScope(scope), item is { } g ? new ItemId(g) : null), ct);

    /// <summary>Tax assessments spec §6: the same view as <c>GET /api/v1/tax/assessments</c>.</summary>
    [McpServerTool(Name = "list_tax_years", ReadOnly = true)]
    [Description("The entered tax notices (avis d'imposition) with their ids and versions: by default the household's joint notices only. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> ListTaxYears(IServiceProvider services,
        [Description("'household' (default): joint notices only; 'me': every notice you declare, solo ones included.")] string? scope = null,
        [Description("One revenue year only.")] int? year = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new ListTaxAssessmentsQuery(SubscriptionEndpoints.ParseScope(scope), year), ct);
}
