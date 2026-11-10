using Nid.Core.Application.Inflation;
using Nid.Core.Application.Scenarios;
using Nid.Core.Contracts;
using Nid.Core.Domain.Projection;
using NodaTime;

namespace Nid.Core.Application.Projection;

/// <summary>
/// Spec §7: a point of the household projection (the purchase scenario has its own <c>ProjectionPointView</c>), amounts in the
/// reference currency rounded to the cent; <c>Real</c> and the <c>Real…</c> parts in today's money.
/// </summary>
public sealed record HouseholdPointView(
    LocalDate On, string Nominal, string Real, string Savings, string Property, string Debt, string RealSavings, string RealProperty, string RealDebt);

/// <summary>Spec §2.3: a retirement the reader can project to, an adult's within 40 years; <c>On</c> is that horizon's last day.</summary>
public sealed record RetirementHorizonView(Guid Person, string Name, LocalDate On);

/// <summary>
/// Spec §3.5: <c>loan_end</c>, <c>property_free</c>, <c>retirement</c>, <c>financial_independence</c>, <c>savings_exhausted</c> or a scenario's <c>event</c> (its label, or
/// its kind); <c>Item</c> is the loan, property or purchase scenario.
/// </summary>
public sealed record ProjectionMilestoneView(LocalDate On, string Kind, string Label, ItemId? Item);

/// <summary>
/// Spec §3.4: <c>income_end_extended</c> (the source's name), <c>savings_exhausted</c> (<c>yyyy-MM</c>), <c>fx_missing</c> (the currency);
/// a scenario adds <c>hidden_targets</c>, <c>event_in_past</c> and <c>event_undated</c> (a count), <c>purchase_in_past</c> and
/// <c>tax_estimate_unavailable</c>.
/// </summary>
public sealed record ProjectionWarningView(string Code, string? Detail);

/// <summary>Ranges spec §4: a point's 10th, 50th and 90th percentiles over the runs, nominal and in today's money.</summary>
public sealed record RangePointView(LocalDate On, string Low, string Median, string High, string LowReal, string MedianReal, string HighReal);

/// <summary>
/// Ranges spec §4: the bands on the projection's grid; <c>Success</c> is the share of runs whose available savings last to the horizon,
/// a whole percent (<c>"87"</c>).
/// </summary>
public sealed record ProjectionRangesView(int Runs, string Success, IReadOnlyList<RangePointView> Points)
{
    internal static ProjectionRangesView From(ProjectionRangesResult r)
    {
        static string Amount(decimal v) => Scenarios.PropertyPurchaseView.Amount(v);
        return new ProjectionRangesView(
            r.Runs, Math.Round(r.Success * 100m, 0, MidpointRounding.AwayFromZero).ToString(System.Globalization.CultureInfo.InvariantCulture),
            r.Points.Select(p => new RangePointView(p.On, Amount(p.Low), Amount(p.Median), Amount(p.High), Amount(p.LowReal), Amount(p.MedianReal),
                Amount(p.HighReal))).ToList());
    }
}

/// <summary>
/// Spec §7: the base projection. <c>MonthlySavings</c> is the S₀ applied, from the household's assumptions (<c>"household"</c>), the
/// last 12 months' average (<c>"auto"</c>) or a scenario's own (<c>"scenario"</c>); <c>Horizon</c> is the wire form asked,
/// <c>HorizonOn</c> the last point's date; <c>RetirementHorizons</c> the retirements the route accepts as a horizon, whatever the horizon asked.
/// <c>ObservedInflation</c> (inflation spec D5): the observed rate the current year uses, a percent with one decimal (<c>"3.1"</c>), or null.
/// <c>FinancialIndependence</c> (retirement spec §3): the first month the test holds, <c>yyyy-MM</c>, or null; <c>Ranges</c> (ranges spec §4): the bands, only when asked.
/// </summary>
public sealed record ProjectionView(
    string Currency, string Horizon, LocalDate HorizonOn, ProjectionAssumptionsView Assumptions, string MonthlySavings, string SavingsOrigin,
    IReadOnlyList<HouseholdPointView> Points, IReadOnlyList<ProjectionMilestoneView> Milestones, IReadOnlyList<ProjectionWarningView> Warnings,
    IReadOnlyList<RetirementHorizonView> RetirementHorizons, string? ObservedInflation, string? FinancialIndependence,
    ProjectionRangesView? Ranges = null)
    : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Milestones.Select(m => m.Item).OfType<ItemId>();

    /// <summary>
    /// <paramref name="extra"/>: a scenario's warnings (spec §3.4), after the reading's and in that order; <paramref name="ranges"/>: the
    /// bands of the same inputs.
    /// </summary>
    internal static ProjectionView From(
        ProjectionHorizon horizon, ProjectionReading reading, HouseholdProjectionResult result, IReadOnlyList<ProjectionWarningView>? extra = null,
        ProjectionRangesResult? ranges = null)
    {
        static string Amount(decimal v) => PropertyPurchaseView.Amount(v);
        var warnings = reading.Extended.Select(e => new ProjectionWarningView("income_end_extended", e.Name)).ToList();
        if (result.SavingsExhaustedIn is { } month) warnings.Add(new("savings_exhausted", $"{month.Year:0000}-{month.Month:00}"));
        warnings.AddRange(reading.Missing.Select(c => new ProjectionWarningView("fx_missing", c)));
        warnings.AddRange(extra ?? []);
        return new ProjectionView(
            reading.Reference.Code, horizon.ToWire(), reading.HorizonOn, ProjectionAssumptionsView.From(reading.Inputs.Assumptions, reading.Reference.Code),
            Amount(reading.Inputs.MonthlySavings), reading.SavingsOrigin,
            result.Points.Select(p => new HouseholdPointView(p.On, Amount(p.Nominal), Amount(p.Real), Amount(p.Savings), Amount(p.Property),
                Amount(p.Debt), Amount(p.SavingsReal), Amount(p.PropertyReal), Amount(p.DebtReal))).ToList(),
            result.Milestones.Select(m => new ProjectionMilestoneView(m.On, m.Kind, m.Label, m.Item)).ToList(),
            warnings, reading.Retirements, reading.ObservedInflation is { } observed ? CpiViews.Percent(observed) : null,
            result.FinancialIndependenceIn is { } fi ? $"{fi.Year:0000}-{fi.Month:00}" : null,
            ranges is null ? null : ProjectionRangesView.From(ranges));
    }
}
