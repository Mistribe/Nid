using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nid.Core.Application.Inflation;
using Nid.Core.Application.Projection;
using Nid.Core.Application.Scenarios;
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
    [Description("The household's projected net worth to a horizon, at today, each December 31st and the horizon's last month, in nominal and today's money, with milestones (loan ends, departures) and the assumptions used, and financialIndependence: the first month (yyyy-MM) the capital's withdrawals at the withdrawal rate, plus pensions and rents, cover the commitments plus the assumed living cost, or null when not reached before the horizon or when assumptions.monthlyLivingCost is 0 (no living cost set). Indicative: assumed returns, no future tax. With scenarioId, a saved projection scenario: the base and the scenario at year ends, the gap at the horizon, milestones and warnings. With ranges, 1,000 runs with random yearly returns (one market factor, each class's volatility) add the 10th, 50th and 90th percentiles at the same points and the share of runs whose savings last to the horizon (the scenario's only, with scenarioId). " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetProjection(IServiceProvider services,
        [Description("'household' (default) or 'me'. Ignored with scenarioId: the scenario's own scope governs.")] string? scope = null,
        [Description("'10y' (default), '5y', '20y', '30y', 'Ny' (1 to 40) or 'retire:<personId>'. Ignored with scenarioId: the scenario's stored horizon governs.")] string? horizon = null,
        [Description("A saved projection scenario's id; a purchase scenario's id is not found here.")] Guid? scenarioId = null,
        [Description("true adds the percentile bands and the probability the savings last (slower).")] bool ranges = false,
        CancellationToken ct = default)
        => scenarioId is { } id
            ? McpCalls.RunAsync(services, () => new GetProjectionScenarioQuery(new ItemId(id), ranges), view => Trim(view.Result), ct)
            : McpCalls.RunAsync(services, () => new GetProjectionQuery(SubscriptionEndpoints.ParseScope(scope), ProjectionEndpoints.ParseHorizon(horizon), ranges),
                YearEnds, ct);

    [McpServerTool(Name = "simulate_projection", ReadOnly = true)]
    [Description("Simulates a « what if » on the household's projected net worth without saving anything: assumption overrides and dated events (income_change, savings_change, early_repayment, property_sale, property_purchase). Returns the base and the scenario at year ends, the gap at the horizon, milestones and warnings. Indicative. " + McpSetup.Untrusted)]
    public static async Task<CallToolResult> SimulateProjection(IServiceProvider services,
        [Description("'household' (default) or 'me'.")] string? scope = null,
        [Description("'10y' (default), '5y', '20y', '30y', 'Ny' (1 to 40) or 'retire:<personId>'.")] string? horizon = null,
        [Description("Assumption overrides, each optional: { classRates: { cash: '2' }, inflation: '1.8', monthlySavings: '500', savingsClass: 'cash', freedPaymentsToSavings: true, inflationByYear: { '2026': '2.5' }, classRatesByYear: { cash: { '2026': '3' } }, incomeGrowth: '0.5', classVolatility: { equity: '16' }, withdrawalRate: '4', monthlyLivingCost: '2000' }. Rates are percents, amounts decimal strings; omitted fields keep the household's. The yearly rates are for calendar years from this one to 4 years later (cash and money_market only for classes); inflationByYear replaces the household's table whole, classRatesByYear merges class by class; incomeGrowth is the income growth over inflation, a percent; classVolatility sets a class's annual volatility (0 to 50), merged class by class; withdrawalRate is the share of the capital drawn each year in financial independence, a percent above 0 and at most 10; monthlyLivingCost is the monthly living cost in today's money, an amount.")] ProjectionOverridesView? assumptions = null,
        [Description("The events, each { kind, start: { on: 'yyyy-mm-dd' } or { person, age }, until?, label?, monthlyAmount? (income_change, savings_change), amount?, split?, loan?, effect?, penalty? (early_repayment), property?, price?, costs? (property_sale), scenario? (property_purchase of a saved purchase scenario; a purchase still needs a start, ignored: the purchase scenario's own date counts) }. Amounts are decimal strings.")] IReadOnlyList<ProjectionEventView>? events = null,
        [Description("The departures, each { person, years? (50 to 75), months? (0 to 11, with years), monthlyPension? }: an adult's age at departure in this scenario, replacing the household's, and optionally their monthly pension in today's money (a decimal string, not negative). Years and months are optional (null keeps the household's departure), but an entry sets an age or a pension. One per adult; the person must be an adult of the household.")] IReadOnlyList<ScenarioRetirementView>? retirements = null,
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

        var read = ProjectionDocumentView.Read(new ProjectionDocumentView(1, horizon ?? ProjectionHorizon.Default.ToWire(), assumptions, events ?? [], retirements ?? []));
        if (read.TryGetFailure(out var invalid))
            return McpCalls.Error(Fields(invalid!));
        read.TryGetValue(out var document);

        var result = await services.GetRequiredService<IInvoker>().InvokeAsync(new SimulateProjectionScenarioQuery(visibility, document!), ct);
        if (result.TryGetFailure(out var failure))
            return failure is NotFoundFailure or UnauthorizedFailure ? McpCalls.Error(McpCalls.NotFound) : McpCalls.Error(Fields(failure!));
        result.TryGetValue(out var view);
        return McpCalls.Answer(services, Trim(view!));
    }

    [McpServerTool(Name = "simulate_property_sale", ReadOnly = true)]
    [Description("What selling a property would leave the household, without saving anything: the net seller amount (price − agency fees − the capital gains tax as get_sale_tax_estimate computes it, sold on that date − the property's loans repaid at their balance − the early repayment penalties given), the monthly loan payments and charges less rent that stop, and the net worth without and with the sale at year ends on the household's assumptions. With scenarioId, a saved sale scenario instead. Indicative. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> SimulatePropertySale(IServiceProvider services,
        [Description("'household' (default) or 'me'. Ignored with scenarioId.")] string? scope = null,
        [Description("The property's id (list_properties).")] Guid? property = null,
        [Description("The sale date, yyyy-mm-dd.")] string? saleOn = null,
        [Description("The sale price, a decimal string.")] string? price = null,
        [Description("The agency fees the seller pays, a decimal string (default 0).")] string? agencyFees = null,
        [Description("The early repayment penalties of the property's loans, a decimal string (default 0): never guess them.")] string? penalties = null,
        [Description("The net worth's horizon in years, 1 to 30 (default 10).")] int? horizonYears = null,
        [Description("A saved sale scenario's id; the other parameters are then ignored.")] Guid? scenarioId = null,
        CancellationToken ct = default)
    {
        if (scenarioId is { } id)
            return McpCalls.RunAsync(services, () => new GetSaleScenarioQuery(new ItemId(id)), view => view.Result, ct);
        static decimal Amount(string? value, string name)
            => value is null ? 0m
                : decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) && d >= 0m ? d
                : throw new BadHttpRequestException($"{name} is not a decimal string.");
        return McpCalls.RunAsync(services, () => new SimulateSaleScenarioQuery(SubscriptionEndpoints.ParseScope(scope), new SaleScenario(
            1, new ItemId(property ?? throw new BadHttpRequestException("property is required.")),
            NodaTime.Text.LocalDatePattern.Iso.Parse(saleOn ?? "").TryGetValue(default, out var on) ? on : throw new BadHttpRequestException("saleOn is not a yyyy-mm-dd date."),
            Amount(price ?? throw new BadHttpRequestException("price is required."), "price"), Amount(agencyFees, "agencyFees"), Amount(penalties, "penalties"),
            horizonYears ?? 10)), ct);
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
            Ranges = view.Ranges is { } r ? r with { Points = r.Points.Where((p, i) => IsKept(p.On, i, r.Points.Count)).ToList() } : null,
        };

    [McpServerTool(Name = "get_inflation", ReadOnly = true)]
    [Description("France's observed inflation (INSEE CPI): the last published month, the last 12 months and the last ten years' annual averages. " + McpSetup.Untrusted)]
    public static Task<CallToolResult> GetInflation(IServiceProvider services, CancellationToken ct = default)
        => McpCalls.RunAsync(services, () => new GetInflationQuery(), ct);
}
