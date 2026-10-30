using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nid.Core.Application.Projection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;
using Nid.Host.Api;

namespace Nid.Host.Mcp;

[McpServerToolType]
public sealed class ProjectionTools
{
    [McpServerTool(Name = "get_projection", ReadOnly = true)]
    [Description("The household's projected net worth to a horizon, at today, each December 31st and the horizon's last month, in nominal and today's money, with milestones (loan ends) and the assumptions used. Indicative: assumed returns, no future tax. With scenarioId, a saved projection scenario: the base and the scenario at year ends, the gap at the horizon, milestones and warnings. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetProjection(IServiceProvider services,
        [Description("'household' (default) or 'me'. Ignored with scenarioId: the scenario's own scope governs.")] string? scope = null,
        [Description("'10y' (default), '5y', '20y', '30y', 'Ny' (1 to 40) or 'retire:<personId>'. Ignored with scenarioId: the scenario's stored horizon governs.")] string? horizon = null,
        [Description("A saved projection scenario's id; a purchase scenario's id is not found here.")] Guid? scenarioId = null,
        CancellationToken ct = default)
        => scenarioId is { } id
            ? McpCalls.RunAsync(services, () => new GetProjectionScenarioQuery(new ItemId(id)), view => Trim(view.Result), ct)
            : McpCalls.RunAsync(services, () => new GetProjectionQuery(SubscriptionEndpoints.ParseScope(scope), ProjectionEndpoints.ParseHorizon(horizon)),
                YearEnds, ct);

    [McpServerTool(Name = "simulate_projection", ReadOnly = true)]
    [Description("Simulates a « what if » on the household's projected net worth without saving anything: assumption overrides and dated events (income_change, savings_change, early_repayment, property_sale, property_purchase). Returns the base and the scenario at year ends, the gap at the horizon, milestones and warnings. Indicative. " + McpSetup.Untrusted)]
    public static async Task<CallToolResult> SimulateProjection(IServiceProvider services,
        [Description("'household' (default) or 'me'.")] string? scope = null,
        [Description("'10y' (default), '5y', '20y', '30y', 'Ny' (1 to 40) or 'retire:<personId>'.")] string? horizon = null,
        [Description("Assumption overrides, each optional: { classRates: { cash: '2' }, inflation: '1.8', monthlySavings: '500', savingsClass: 'cash', freedPaymentsToSavings: true }. Rates are percents, amounts decimal strings; omitted fields keep the household's.")] ProjectionOverridesView? assumptions = null,
        [Description("The events, each { kind, start: { on: 'yyyy-mm-dd' } or { person, age }, until?, label?, monthlyAmount? (income_change, savings_change), amount?, split?, loan?, effect?, penalty? (early_repayment), property?, price?, costs? (property_sale), scenario? (property_purchase of a saved purchase scenario; a purchase still needs a start, ignored: the purchase scenario's own date counts) }. Amounts are decimal strings.")] IReadOnlyList<ProjectionEventView>? events = null,
        CancellationToken ct = default)
    {
        VisibilityScope visibility;
        try
        {
            visibility = SubscriptionEndpoints.ParseScope(scope);
        }
        catch (BadHttpRequestException unreadable)
        {
            return McpCalls.Error(unreadable.Message);
        }

        var read = ProjectionDocumentView.Read(new ProjectionDocumentView(1, horizon ?? ProjectionHorizon.Default.ToWire(), assumptions, events ?? []));
        if (read.TryGetFailure(out var invalid))
            return McpCalls.Error(Fields(invalid!));
        read.TryGetValue(out var document);

        var result = await services.GetRequiredService<IInvoker>().InvokeAsync(new SimulateProjectionScenarioQuery(visibility, document!), ct);
        if (result.TryGetFailure(out var failure))
            return failure is NotFoundFailure or UnauthorizedFailure ? McpCalls.Error(McpCalls.NotFound) : McpCalls.Error(Fields(failure!));
        result.TryGetValue(out var view);
        return McpCalls.Answer(services, Trim(view!));
    }

    /// <summary>The refused fields, each named (<c>events[0].amount: …</c>); a refusal without fields, its code and message.</summary>
    private static string Fields(Failure failure)
        => FieldErrors.Of(failure) is { Count: > 0 } fields
            ? "Refused: " + string.Join("; ", fields.Select(f => $"{f.Path}: {f.Message}"))
            : $"Refused: {failure.Code}. {failure.Message}";

    private static ProjectionComparisonView Trim(ProjectionComparisonView view)
        => view with
        {
            Base = YearEnds(view.Base), Scenario = YearEnds(view.Scenario),
            Gaps = view.Gaps.Where((g, i) => IsKept(g.On, i, view.Gaps.Count)).ToList(),
        };

    /// <summary>Today's point (the first), each 31 December and the last.</summary>
    private static bool IsKept(NodaTime.LocalDate on, int index, int count) => index == 0 || index == count - 1 || on is { Month: 12, Day: 31 };

    /// <summary>Spec §7: the assistant reads the year ends (31 December), with today's point and the last; the rest is the route's answer.</summary>
    internal static ProjectionView YearEnds(ProjectionView view)
        => view with
        {
            Points = view.Points.Where((p, i) => IsKept(p.On, i, view.Points.Count)).ToList(),
        };
}
