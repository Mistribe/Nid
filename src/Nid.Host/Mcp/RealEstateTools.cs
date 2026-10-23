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
        => McpCalls.RunAsync(services, () => new GetPropertyQuery(new ItemId(id)), ct);

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
