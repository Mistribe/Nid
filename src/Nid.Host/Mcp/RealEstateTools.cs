using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nid.Core.Contracts;
using Nid.Host.Api;
using Nid.RealEstate.Application;

namespace Nid.Host.Mcp;

[McpServerToolType]
public sealed class RealEstateTools
{
    [McpServerTool(Name = "list_loans", ReadOnly = true)]
    [Description("The loans with their monthly payment and remaining balance. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> ListLoans(IServiceProvider services,
        [Description("'household' (default) or 'me'.")] string? scope = null, [Description("Include ended loans.")] bool ended = false,
        CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new ListLoansQuery(SubscriptionEndpoints.ParseScope(scope), ended), ct);

    [McpServerTool(Name = "get_loans_overview", ReadOnly = true)]
    [Description("The household's loans together: remaining balance, monthly payments, interest still due and when each loan ends. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetLoansOverview(IServiceProvider services,
        [Description("'household' (default) or 'me'.")] string? scope = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetLoansOverviewQuery(SubscriptionEndpoints.ParseScope(scope)), ct);

    [McpServerTool(Name = "get_loan", ReadOnly = true)]
    [Description("One loan: its contract, phases, insurances, fees, early repayments and figures today. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetLoan(IServiceProvider services, [Description("The loan's id.")] Guid id, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetLoanQuery(new ItemId(id)), ct);

    [McpServerTool(Name = "get_loan_schedule", ReadOnly = true)]
    [Description("A loan's amortisation schedule: each payment split into interest, principal and insurance, with the remaining balance. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetLoanSchedule(IServiceProvider services, [Description("The loan's id.")] Guid id, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetLoanScheduleQuery(new ItemId(id)), ct);

    [McpServerTool(Name = "list_properties", ReadOnly = true)]
    [Description("The properties with their latest value and loans. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> ListProperties(IServiceProvider services,
        [Description("'household' (default) or 'me'.")] string? scope = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new ListPropertiesQuery(SubscriptionEndpoints.ParseScope(scope)), ct);

    [McpServerTool(Name = "get_property", ReadOnly = true)]
    [Description("One property: its details, valuations, flows, recurring costs and tenancies. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetProperty(IServiceProvider services, [Description("The property's id.")] Guid id, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetPropertyQuery(new ItemId(id)), WithoutIdentity, ct);

    /// <summary>Rental receipts spec §2: the postal address and the parties' names and address serve receipts and letters, never MCP.</summary>
    private static PropertyDetail WithoutIdentity(PropertyDetail detail) => detail with
    {
        PostalAddress = null,
        Tenancies = detail.Tenancies.Select(t => t with { TenantNames = null, LandlordName = null, LandlordAddress = null }).ToList(),
    };

    [McpServerTool(Name = "get_rent_revisions", ReadOnly = true)]
    [Description("The yearly rent revisions due (IRL): each tenancy's anniversary, the two IRL values, the current and the new rent. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetRentRevisions(IServiceProvider services,
        [Description("'household' (default) or 'me'.")] string? scope = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetRentRevisionsQuery(SubscriptionEndpoints.ParseScope(scope)), ct);

    [McpServerTool(Name = "get_rent_arrears", ReadOnly = true)]
    [Description("The rents still due per property, one account per tenant: each month's due, imputed and remaining amounts and whether it is overdue (ended more than 10 days ago); a month two tenants share appears once per tenant. Properties with nothing due are omitted. No names or addresses. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetRentArrears(IServiceProvider services,
        [Description("'household' (default) or 'me'.")] string? scope = null, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetRentArrearsQuery(SubscriptionEndpoints.ParseScope(scope)), ct);

    [McpServerTool(Name = "simulate_early_repayment", ReadOnly = true)]
    [Description("Simulates an early repayment of a loan without recording it: the new term or payment and the interest saved. " + McpSetup.Untrusted)]
    public static async Task<CallToolResult> SimulateEarlyRepayment(IServiceProvider services, [Description("The loan's id.")] Guid id,
        [Description("ISO date of the repayment.")] string on, [Description("Amount, a decimal string (e.g. '10000.00').")] string amount,
        [Description("'reduce_term' or 'reduce_payment'; omitted, both are compared.")] string? effect = null,
        [Description("Penalty, a decimal string; default 0.")] string? penalty = null, CancellationToken ct = default)
    {
        var reader = new FieldReader();
        var query = new RealEstateEndpoints.LoanSimulationBody(on, amount, effect, penalty).ToQuery(new ItemId(id), reader);
        return query is null
            ? McpCalls.Error(string.Join("; ", reader.Errors.Select(e => $"{e.Path}: {e.Message}")))
            : await McpCalls.RunAsync(services, () => query, ct);
    }

    [McpServerTool(Name = "simulate_loan_renegotiation", ReadOnly = true)]
    [Description("Simulates renegotiating a loan at a new rate without recording it: the loan as it runs and renegotiated side by side (payment, end, interest left, total cost with fees) and the payment from which the renegotiation pays for itself, null when it never does. " + McpSetup.Untrusted)]
    public static async Task<CallToolResult> SimulateLoanRenegotiation(IServiceProvider services, [Description("The loan's id.")] Guid id,
        [Description("ISO date: the new rate applies from the first payment after it.")] string on,
        [Description("The new annual rate as a decimal fraction string (e.g. '0.025' for 2.5 %).")] string annualRate,
        [Description("Payments left after the date; omitted, the same as now.")] int? payments = null,
        [Description("Bank fees, a decimal string; default 0.")] string? bankFees = null,
        [Description("Guarantee fees, a decimal string; default 0.")] string? guaranteeFees = null,
        [Description("Early repayment penalty as the bank states it, a decimal string; default 0.")] string? penalty = null, CancellationToken ct = default)
    {
        var reader = new FieldReader();
        var query = new RealEstateEndpoints.LoanRenegotiationBody(on, annualRate, payments, bankFees, guaranteeFees, penalty).ToQuery(new ItemId(id), reader);
        return query is null
            ? McpCalls.Error(string.Join("; ", reader.Errors.Select(e => $"{e.Path}: {e.Message}")))
            : await McpCalls.RunAsync(services, () => query, ct);
    }

    [McpServerTool(Name = "compare_loan_insurance", ReadOnly = true)]
    [Description("Compares a loan's borrower insurance with quotes without recording anything: per insured person (insurance line), what is left to pay on the payments after the date (one due on it counts as made, as for a renegotiation) with the current contract and with the quote. " + McpSetup.Untrusted)]
    public static async Task<CallToolResult> CompareLoanInsurance(IServiceProvider services, [Description("The loan's id.")] Guid id,
        [Description("ISO date of the change: the quote takes over from the first payment after it.")] string on,
        [Description("Quotes: { insurance (the index of the line in get_loan's contract.insurances), mode ('fixed_monthly', 'rate_on_initial' or 'rate_on_remaining'), monthlyAmount? (decimal string, fixed_monthly), annualRate? (decimal fraction string, the rated modes) }.")]
        IReadOnlyList<RealEstateEndpoints.InsuranceQuoteBody> quotes, CancellationToken ct = default)
    {
        var reader = new FieldReader();
        var query = new RealEstateEndpoints.LoanInsuranceComparisonBody(on, quotes).ToQuery(new ItemId(id), reader);
        return query is null
            ? McpCalls.Error(string.Join("; ", reader.Errors.Select(e => $"{e.Path}: {e.Message}")))
            : await McpCalls.RunAsync(services, () => query, ct);
    }

    [McpServerTool(Name = "simulate_household_early_repayment", ReadOnly = true)]
    [Description("Simulates an early repayment split across the household's loans without recording it: what goes to each loan and the interest saved. " + McpSetup.Untrusted)]
    public static async Task<CallToolResult> SimulateHouseholdEarlyRepayment(IServiceProvider services,
        [Description("ISO date of the repayment.")] string on, [Description("Amount, a decimal string (e.g. '20000.00').")] string amount,
        [Description("'highest_rate', 'smallest_balance' or 'chosen' (everything on loan_id).")] string split,
        [Description("'reduce_term' or 'reduce_payment'.")] string effect,
        [Description("The loan's id, with the 'chosen' split only.")] Guid? loanId = null,
        [Description("Penalties per loan: { loanId, amount } with amount a decimal string.")] IReadOnlyList<RealEstateEndpoints.LoanPenaltyBody>? penalties = null,
        [Description("'household' (default) or 'me'.")] string? scope = null, CancellationToken ct = default)
    {
        var reader = new FieldReader();
        var query = new RealEstateEndpoints.LoansSimulationBody(scope, on, amount, split, loanId, effect, penalties).ToQuery(reader);
        return query is null
            ? McpCalls.Error(string.Join("; ", reader.Errors.Select(e => $"{e.Path}: {e.Message}")))
            : await McpCalls.RunAsync(services, () => query, ct);
    }
}
