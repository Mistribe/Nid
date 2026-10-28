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

/// <summary>Spec §3.5: <c>loan_end</c>, <c>property_free</c>, <c>retirement</c> or <c>savings_exhausted</c>; <c>Item</c> is the loan or property.</summary>
public sealed record ProjectionMilestoneView(LocalDate On, string Kind, string Label, ItemId? Item);

/// <summary>Spec §3.4: <c>income_end_extended</c> (the source's name), <c>savings_exhausted</c> (<c>yyyy-MM</c>), <c>fx_missing</c> (the currency).</summary>
public sealed record ProjectionWarningView(string Code, string? Detail);

/// <summary>
/// Spec §7: the base projection. <c>MonthlySavings</c> is the S₀ applied, from the household's assumptions (<c>"household"</c>) or the
/// last 12 months' average (<c>"auto"</c>); <c>Horizon</c> is the wire form asked, <c>HorizonOn</c> the last point's date;
/// <c>RetirementHorizons</c> the retirements the route accepts as a horizon, whatever the horizon asked.
/// </summary>
public sealed record ProjectionView(
    string Currency, string Horizon, LocalDate HorizonOn, ProjectionAssumptionsView Assumptions, string MonthlySavings, string SavingsOrigin,
    IReadOnlyList<HouseholdPointView> Points, IReadOnlyList<ProjectionMilestoneView> Milestones, IReadOnlyList<ProjectionWarningView> Warnings,
    IReadOnlyList<RetirementHorizonView> RetirementHorizons)
    : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Milestones.Select(m => m.Item).OfType<ItemId>();

    internal static ProjectionView From(ProjectionHorizon horizon, ProjectionReading reading, HouseholdProjectionResult result)
    {
        static string Amount(decimal v) => PropertyPurchaseView.Amount(v);
        var warnings = reading.Extended.Select(e => new ProjectionWarningView("income_end_extended", e.Name)).ToList();
        if (result.SavingsExhaustedIn is { } month) warnings.Add(new("savings_exhausted", $"{month.Year:0000}-{month.Month:00}"));
        warnings.AddRange(reading.Missing.Select(c => new ProjectionWarningView("fx_missing", c)));
        return new ProjectionView(
            reading.Reference.Code, horizon.ToWire(), reading.HorizonOn, ProjectionAssumptionsView.From(reading.Inputs.Assumptions, reading.Reference.Code),
            Amount(reading.Inputs.MonthlySavings), reading.SavingsOrigin,
            result.Points.Select(p => new HouseholdPointView(p.On, Amount(p.Nominal), Amount(p.Real), Amount(p.Savings), Amount(p.Property),
                Amount(p.Debt), Amount(p.SavingsReal), Amount(p.PropertyReal), Amount(p.DebtReal))).ToList(),
            result.Milestones.Select(m => new ProjectionMilestoneView(m.On, m.Kind, m.Label, m.Item)).ToList(),
            warnings, reading.Retirements);
    }
}
