using System.Text.Json;
using Nid.Ai.Contracts;
using Nid.Host.Mcp;

namespace Nid.Host.Assistant;

/// <summary>
/// Decision I (<c>docs/ai/README.md</c> §8.2, #499): the MCP tools the assistant may call, each with the data classes it
/// returns (§6.1, §6.4), and the ones it never gets. A tool in neither list fails <c>AssistantToolTests</c>.
/// </summary>
public static class AssistantCatalogue
{
    private static readonly IReadOnlySet<string> Figures = new HashSet<string> { DataClasses.Figures };
    private static readonly IReadOnlySet<string> BankLines = new HashSet<string> { DataClasses.Figures, DataClasses.BankLabels };

    /// <summary>#624: a proposal's payload and source label copy a bank line's label or a document's text, a payslip's included.</summary>
    private static readonly IReadOnlySet<string> Proposals = new HashSet<string>
        { DataClasses.Figures, DataClasses.BankLabels, DataClasses.Documents, DataClasses.LocalOnly };

    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Allowed = new Dictionary<string, IReadOnlySet<string>>
    {
        ["get_dashboard"] = Figures, ["get_net_worth_history"] = Figures, ["get_month_changes"] = Figures, ["get_spending"] = Figures,
        ["list_envelopes"] = Figures, ["get_envelope"] = Figures, ["get_envelope_performance"] = Figures,
        ["list_loans"] = Figures, ["get_loan"] = Figures, ["get_loans_overview"] = Figures,
        ["list_properties"] = Figures, ["get_property"] = Figures,
        ["list_income_sources"] = Figures, ["get_income_source"] = Figures,
        ["list_tax_years"] = Figures, ["get_tax"] = Figures,
        ["get_goals"] = Figures, ["get_goal"] = Figures,
        ["list_subscriptions"] = Figures, ["get_subscription"] = Figures,
        ["list_persons"] = Figures, ["list_expected_charges"] = Figures,
        // The bank lines and the two proposals made from them: their previews show the lines' labels.
        ["list_bank_transactions"] = BankLines, ["list_uncategorised_bank_transactions"] = BankLines,
        ["propose_bank_link"] = BankLines, ["propose_bank_categories"] = BankLines,
        ["list_proposals"] = Proposals,
    };

    /// <summary>What a tool's answer makes of the turn's class (§6.1): <c>local_only</c> over <c>bank_labels</c> over the rest. A tool
    /// answering more than figures is never called for the cloud (#624): <c>allowed_kinds</c> does not cover these classes yet.</summary>
    public static string ClassAfter(string current, IReadOnlySet<string> tool)
        => current == DataClasses.LocalOnly || tool.Contains(DataClasses.LocalOnly) ? DataClasses.LocalOnly
            : current == DataClasses.BankLabels || tool.Contains(DataClasses.BankLabels) ? DataClasses.BankLabels
            : current;

    /// <summary>True when the tool answers more than figures: kept from a cloud provider.</summary>
    public static bool KeptFromCloud(IReadOnlySet<string> tool) => tool.Any(c => c != DataClasses.Figures);

    public static readonly IReadOnlySet<string> Excluded = new HashSet<string>(
    [
        // Always: files in or out, and a decision the person takes in the browser.
        "upload_document", "request_document_upload", "read_document", "delete_proposal",
        // Documents wait for the household's allowed_kinds (§6.1); the proposals copied from one need it read.
        "list_documents", "search_documents", "propose_statement", "propose_holdings_snapshot", "propose_breakdown", "propose_payslip", "propose_invoice",
        "propose_amortization",
        // What-ifs and estimates, not the household's figures.
        "simulate_early_repayment", "simulate_household_early_repayment", "simulate_loan_renegotiation", "simulate_projection",
        "simulate_property_sale", "compare_loan_insurance", "get_sale_tax_estimate", "get_projection",
        // Not in the first set (#173): childcare, holdings, routines and the rest.
        "list_childcare", "get_childcare", "get_childcare_statement", "get_childcare_statements", "get_childcare_year", "list_nannies",
        "get_nanny", "get_envelope_holdings", "get_holding_performance", "list_holding_movements", "list_holding_snapshots",
        "get_routine", "get_property_routine", "get_rent_arrears", "get_rent_revisions", "get_loan_schedule", "get_passive_income",
        "get_inflation", "get_tax_changes", "get_rental_2044", "get_year_review", "list_providers", "get_proposal",
        // The route tools write with apply; the assistant's only writes are the propose_* above.
        .. RouteActions.All.Select(a => a.Tool),
    ]);

    /// <summary>The arguments a tool gets from the assistant: never <c>apply</c>, never <c>documentId</c> (AST-11).</summary>
    public static Dictionary<string, JsonElement> Arguments(JsonElement arguments)
        => arguments.ValueKind != JsonValueKind.Object
            ? []
            : arguments.EnumerateObject().Where(p => !Stripped.Contains(p.Name)).ToDictionary(p => p.Name, p => p.Value.Clone());

    internal static readonly IReadOnlySet<string> Stripped = new HashSet<string> { "apply", "documentId" };
}
