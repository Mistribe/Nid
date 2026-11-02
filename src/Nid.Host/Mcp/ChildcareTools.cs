using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nid.Childcare.Application;
using Nid.Core.Contracts;
using Nid.Host.Api;
using NodaTime.Text;

namespace Nid.Host.Mcp;

[McpServerToolType]
public sealed class ChildcareTools
{
    private static readonly YearMonthPattern Month = YearMonthPattern.CreateWithInvariantCulture("uuuu-MM");

    [McpServerTool(Name = "list_childcare", ReadOnly = true)]
    [Description("The childminder arrangements (assistante maternelle): child, carer, monthly base pay, weekly hours, start and end. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> ListChildcare(IServiceProvider services,
        [Description("'household' (default) or 'me'.")] string? scope = null, [Description("Include ended arrangements.")] bool closed = false,
        CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new ListChildcareQuery(SubscriptionEndpoints.ParseScope(scope), closed), ct);

    [McpServerTool(Name = "get_childcare", ReadOnly = true)]
    [Description("One arrangement: its revisions with their weekly schedule, its weeks without care, the paid-leave days accrued, the estimated CMG and its version. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetChildcare(IServiceProvider services, [Description("The arrangement's id.")] Guid id, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetChildcareQuery(new ItemId(id)), ct);

    [McpServerTool(Name = "get_childcare_statements", ReadOnly = true)]
    [Description("An arrangement's months, newest first: none, draft or validated, and a validated month's cost. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetChildcareStatements(IServiceProvider services, [Description("The arrangement's id.")] Guid id, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new ListStatementsQuery(new ItemId(id)), ct);

    [McpServerTool(Name = "get_childcare_statement", ReadOnly = true)]
    [Description("One month's statement: every day (planned, present, times, meal, absence reason), the mileage, the CMG, the Pajemploi declaration "
        + "field by field and its version. Its days are what propose_save_childcare_statement sends back, the deviations changed. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetChildcareStatement(IServiceProvider services, [Description("The arrangement's id.")] Guid id,
        [Description("The month, yyyy-MM.")] string month, CancellationToken ct = default)
        => Month.Parse(month) is { Success: true } parsed
            ? McpCalls.RunAsync(services, () => new GetStatementQuery(new ItemId(id), parsed.Value), ct)
            : Task.FromResult(McpCalls.Error("month: a month is yyyy-MM."));
}
