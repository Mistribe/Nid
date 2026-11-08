using Nid.Core.Contracts;
using Nid.Investments.Application.Prices;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Investments.Application;

/// <summary>
/// Sale tax spec §4 and §6: the visible envelopes, of every kind — their payments and withdrawals, valuations and today's value (the
/// estimate when <see cref="TodayValue"/> picks it), and the lines of their latest snapshot; the closed ones for their flows and values
/// only. Facts that cannot be read leave every envelope without a value or lines, as on the dashboard.
/// </summary>
public sealed class EnvelopesSale(
    IVisibleItems visible, IEnvelopeStore envelopes, EnvelopePerformance performance, IItemRegistry items, IHoldingStore holdings,
    EnvelopeEstimator estimator) : ISaleContribution
{
    public async Task<Result<SaleAssets>> SaleAssetsAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct)
    {
        // Closed envelopes too (closing archives the item): a transfer's source is often one. Every envelope the reader sees, whatever
        // the scope asked, is read for the transfers' counterparts; only those of the scope are supplied.
        var seen = await visible.ForAsync(reader, VisibilityScope.Me, includeArchived: true, ct);
        var asked = scope == VisibilityScope.Me ? seen : await visible.ForAsync(reader, scope, includeArchived: true, ct);
        var found = await envelopes.FindManyAsync(seen.ToList(), ct);
        var accesses = await items.FindManyAsync(found.Select(e => e.ItemId).ToList(), ct);
        var facts = await performance.FactsManyAsync(found, accesses, ct) ?? new Dictionary<ItemId, EnvelopePerformance.Facts>();
        var open = found.Where(e => asked.Contains(e.ItemId) && e.ClosedOn is null).Select(e => e.ItemId).ToList();
        var export = await holdings.ExportAsync(open, ct);
        var estimates = await estimator.EstimateAsync(open, ct);
        // Transfer legs by reference: a leg's counterpart is the other one there.
        var legs = facts.Values
            .SelectMany(f => f.Flows.Where(InvestmentsOverviewMath.IsTransfer).Select(flow => (flow.Id, flow.ExternalRef, Kind: EnvelopeWire.Of(f.Envelope.Kind))))
            .ToLookup(l => l.ExternalRef);

        var list = new List<EnvelopeForSale>();
        foreach (var envelope in found.Where(e => asked.Contains(e.ItemId)))
        {
            if (!accesses.TryGetValue(envelope.ItemId, out var item))
                continue;
            var f = facts.GetValueOrDefault(envelope.ItemId);
            var figures = f is null || envelope.ClosedOn is not null
                ? null
                : EnvelopeMath.At(f.Values, f.Flows, f.Currency, f.Envelope.Cadence, f.Envelope.StaleAfterDays, today);
            var picked = figures?.Value is null ? null : ((Money Value, LocalDate AsOf, bool Estimated, IReadOnlyList<EstimatedLine>? Lines)?)
                TodayValue.Pick(figures, estimates.GetValueOrDefault(envelope.ItemId));
            var flows = (f?.Flows ?? [])
                .Where(x => x.OccurredOn <= today && x.Kind is FlowKind.Opening or FlowKind.Contribution or FlowKind.Withdrawal)
                .Select(x =>
                {
                    var reference = InvestmentsOverviewMath.IsTransfer(x) ? x.ExternalRef : null;
                    var counterpart = reference is null ? null : legs[reference].Where(l => l.Id != x.Id).Select(l => l.Kind).FirstOrDefault();
                    return new SaleFlow(x.OccurredOn, EnvelopeWire.FlowWire(x.Kind), x.Amount.Amount, reference, counterpart);
                })
                .ToList();
            var values = (f?.Values ?? []).Where(v => v.AsOf <= today).Select(v => new SaleValue(v.AsOf, v.Value.Amount)).ToList();
            // No lines past the facts' gate, as on the dashboard.
            var snapshot = figures is null ? null : EnvelopeAllocation.Latest(export.Snapshots, envelope.ItemId, today);
            // An estimated value comes with each holding's estimated value.
            var lines = snapshot?.Lines
                .Select(l => new SaleLine(picked?.Lines?.FirstOrDefault(e => e.HoldingId == l.HoldingId)?.Value ?? l.Value, l.Units, l.CostBasisPerUnit))
                .ToList();
            list.Add(new EnvelopeForSale(item.Id, item.DisplayName, item.Currency, item.Owners, EnvelopeWire.Of(envelope.Kind), envelope.OpenedOn,
                envelope.PremiumsBefore2017, picked?.Value.Amount, picked?.AsOf, picked is not null && figures!.StaleDays is not null,
                picked?.Estimated ?? false, flows, values, lines, snapshot?.AsOf, snapshot?.Complete ?? false, envelope.ClosedOn));
        }
        return Result.Success(new SaleAssets([], list));
    }
}
