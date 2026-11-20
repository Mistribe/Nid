using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Investments.Application;

/// <summary>
/// #234: the capital income the year's envelopes give beyond their income flows. A savings account that is no regulated livret earns
/// taxable interest: its performance over the year (values against payments and withdrawals), as <see cref="TaxLineCodes.SavingsInterest"/>.
/// One whose balance comes from the bank, or that has no payment recorded, cannot tell interest from deposits: <see cref="TaxLineCodes.SavingsInterestUnknown"/>.
/// A brokerage account's sales of the year (a holding's units going out) gain their amount minus the units at the cost per unit of the
/// latest snapshot before them, netted per account as <see cref="TaxLineCodes.RealisedGain"/>; a sale without units or cost per unit
/// gives <see cref="TaxLineCodes.RealisedGainUnknown"/> with its amount.
/// </summary>
public sealed class CapitalTaxContribution(
    IVisibleItems visible, IItemRegistry items, IEnvelopeStore envelopes, EnvelopePerformance performance, IHoldingStore holdings) : ITaxContribution
{
    public async Task<Result<IReadOnlyList<TaxLine>>> TaxLinesAsync(PersonId reader, VisibilityScope scope, int year, LocalDate today, CancellationToken ct)
    {
        var (start, end) = (new LocalDate(year, 1, 1), new LocalDate(year, 12, 31));
        var found = (await envelopes.FindManyAsync((await visible.ForAsync(reader, scope, includeArchived: true, ct)).ToList(), ct))
            .Where(e => (e.OpenedOn is null || e.OpenedOn <= end) && (e.ClosedOn is null || e.ClosedOn >= start))
            .Where(e => e.Kind == EnvelopeKind.Brokerage || e.Kind == EnvelopeKind.Savings && e.Regulated is null)
            .ToList();
        var accesses = await items.FindManyAsync(found.Select(e => e.ItemId).ToList(), ct);
        var lines = new List<TaxLine>();
        var inEuros = found.Where(e => accesses.TryGetValue(e.ItemId, out var item) && TaxLineCodes.InEuros(item, lines)).ToList();

        var savings = inEuros.Where(e => e.Kind == EnvelopeKind.Savings).ToList();
        if (savings.Count > 0)
        {
            // Facts that cannot be read leave each account's interest unknown, as the dashboard leaves its value.
            var facts = await performance.FactsManyAsync(savings, accesses, ct);
            foreach (var e in savings)
                lines.Add(facts?.GetValueOrDefault(e.ItemId) is { } f ? SavingsLine(f, start, end)
                    : new TaxLine(TaxLineCodes.SavingsInterestUnknown, e.ItemId, accesses[e.ItemId].DisplayName, null, 0m));
        }

        var brokerage = inEuros.Where(e => e.Kind == EnvelopeKind.Brokerage).Select(e => e.ItemId).ToList();
        if (brokerage.Count > 0)
        {
            var export = await holdings.ExportAsync(brokerage, ct);
            foreach (var account in brokerage)
                lines.AddRange(GainLines(accesses[account], export, start, end));
        }
        return Result.Success<IReadOnlyList<TaxLine>>(lines);
    }

    internal static TaxLine SavingsLine(EnvelopePerformance.Facts f, LocalDate start, LocalDate end)
    {
        TaxLine Unknown() => new(TaxLineCodes.SavingsInterestUnknown, f.Item.Id, f.Item.DisplayName, null, 0m);
        if (!EnvelopeMath.BankFedCovered(f.Values, f.Flows)
            || !f.Flows.Any(x => x.Kind is FlowKind.Opening or FlowKind.Contribution))
            return Unknown();
        // shortcut: the current year counts the interest its values show so far; most banks credit it on 31 December.
        var period = EnvelopeMath.Period(f.Values, f.Flows, f.Currency, start.PlusDays(-1), end);
        if (period.NoStartValue || period.Performance is not { } p)
            return Unknown();
        return new TaxLine(TaxLineCodes.SavingsInterest, f.Item.Id, f.Item.DisplayName, null, Math.Max(0m, p.Amount));
    }

    private static IEnumerable<TaxLine> GainLines(ItemAccess item, HoldingsExport export, LocalDate start, LocalDate end)
    {
        var snapshots = export.Snapshots.Where(s => s.Envelope == item.Id).OrderByDescending(s => s.AsOf).ToList();
        var sales = export.Movements.Where(m => m.Envelope == item.Id && m.OccurredOn >= start && m.OccurredOn <= end
                                                && m.Kind is MovementKinds.AllocationOut or MovementKinds.SwitchOut or MovementKinds.Maturity).ToList();
        decimal gain = 0m, unknown = 0m;
        foreach (var sale in sales)
        {
            // The cost per unit is not changed by a sale: a snapshot of the sale's own day still holds it.
            var cost = snapshots.Where(s => s.AsOf <= sale.OccurredOn).SelectMany(s => s.Lines.Where(l => l.HoldingId == sale.HoldingId))
                .Select(l => l.CostBasisPerUnit).FirstOrDefault(c => c is not null);
            if (sale.Units is { } units && units != 0m && cost is { } perUnit)
                gain += sale.Amount - Math.Abs(units) * perUnit;
            else
                unknown += sale.Amount;
        }
        if (gain != 0m)
            yield return new TaxLine(TaxLineCodes.RealisedGain, item.Id, item.DisplayName, null, Math.Round(gain, 2, MidpointRounding.AwayFromZero));
        if (unknown > 0m)
            yield return new TaxLine(TaxLineCodes.RealisedGainUnknown, item.Id, item.DisplayName, null, unknown);
    }
}
