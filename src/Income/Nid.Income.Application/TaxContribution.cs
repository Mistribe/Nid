using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Income.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Income.Application;

/// <summary>
/// Tax estimate spec §6: each visible source's occurrences in the revenue year (closed sources too: their months still count),
/// the gross where the phase has it, and the one-offs of the year, as net amounts.
/// </summary>
public sealed class IncomeSourcesTax(IVisibleItems visible, IItemRegistry items, ISourceStore sources, IPhaseStore phases, IFlows flows, IOneOffTaxableStore taxables) : ITaxContribution
{
    public async Task<Result<IReadOnlyList<TaxLine>>> TaxLinesAsync(PersonId reader, VisibilityScope scope, int year, LocalDate today, CancellationToken ct)
    {
        var found = await sources.FindManyAsync((await visible.ForAsync(reader, scope, includeArchived: true, ct)).ToList(), ct);
        var ids = found.Select(s => s.ItemId).ToList();
        var bySource = (await phases.ForSourcesAsync(ids, ct)).ToLookup(p => p.Source);
        var accesses = await items.FindManyAsync(ids, ct);
        var range = TaxLineCodes.Year(year);
        var (from, until) = (range.Start, range.EndBefore!.Value);
        var oneOffs = await flows.ListManyAsync(ids, range, new HashSet<FlowKind> { FlowKind.Income }, ct);
        if (oneOffs.TryGetFailure(out var failure)) return Result.Failure<IReadOnlyList<TaxLine>>(failure!);
        oneOffs.TryGetValue(out var byItem);
        var taxableOf = await taxables.ForFlowsAsync(
            byItem!.Values.SelectMany(l => l).Where(f => f.Category == IncomeWire.OneOffCategory).Select(f => f.Id).ToList(), ct);

        var lines = new List<TaxLine>();
        foreach (var source in found)
        {
            if (!accesses.TryGetValue(source.ItemId, out var item) || !TaxLineCodes.InEuros(item, lines))
                continue;
            var occurrences = bySource[source.ItemId].SelectMany(p => IncomeMath.Occurrences(p, from, until).Select(o => (Phase: p, o.Amount, o.On))).ToList();
            var extra = byItem!.GetValueOrDefault(source.ItemId, []).Where(f => f.Category == IncomeWire.OneOffCategory).Sum(f => f.Amount.Amount);
            var salaryOneOffs = byItem!.GetValueOrDefault(source.ItemId, []).Where(f => f.Category == IncomeWire.OneOffCategory).ToList();
            var net = occurrences.Sum(o => o.Amount.Amount);
            switch (source.Kind)
            {
                case IncomeKind.Salary:
                    // Each payment goes to the line of the most precise figure it holds: taxable, else gross, else net.
                    var taxable = occurrences.Where(o => o.Phase.TaxableAmount is not null).ToList();
                    var grossOnly = occurrences.Where(o => o.Phase.TaxableAmount is null && o.Phase.GrossAmount is not null).ToList();
                    var netOnly = occurrences.Where(o => o.Phase.TaxableAmount is null && o.Phase.GrossAmount is null).ToList();
                    var taxedOneOffs = salaryOneOffs.Where(f => taxableOf.ContainsKey(f.Id)).ToList();
                    var plainOneOffs = salaryOneOffs.Where(f => !taxableOf.ContainsKey(f.Id)).ToList();
                    var taxableSum = taxable.Sum(o => o.Phase.TaxableAmount!.Value.Amount) + taxedOneOffs.Sum(f => taxableOf[f.Id]);
                    if (taxableSum > 0)
                        lines.AddRange(TaxLineCodes.Split(TaxLineCodes.SalaryTaxable, item, taxableSum));
                    lines.AddRange(TaxLineCodes.Split(TaxLineCodes.SalaryGross, item, grossOnly.Sum(o => o.Phase.GrossAmount!.Value.Amount)));
                    var netSum = netOnly.Sum(o => o.Amount.Amount);
                    if (netSum > 0)
                        lines.AddRange(TaxLineCodes.Split(TaxLineCodes.SalaryNet, item, netSum));
                    var plainSum = plainOneOffs.Sum(f => f.Amount.Amount);
                    if (plainSum > 0)
                        lines.AddRange(TaxLineCodes.Split(TaxLineCodes.SalaryOneOff, item, plainSum));

                    // One detail line per payment (what the estimate retained, and why), outside every sum.
                    foreach (var o in occurrences.OrderBy(o => o.On).ThenBy(o => o.Phase.Label, StringComparer.Ordinal))
                    {
                        var (amount, category) = o.Phase.TaxableAmount is { } t ? (t.Amount, TaxLineCodes.DetailEntered)
                            : o.Phase.GrossAmount is { } g ? (g.Amount, TaxLineCodes.DetailGross) : (o.Amount.Amount, TaxLineCodes.DetailNet);
                        lines.AddRange(TaxLineCodes.Split(TaxLineCodes.SalaryDetail, item, amount)
                            .Select(l => l with { On = o.On, Note = o.Phase.Note ?? o.Phase.Label, Category = category }));
                        // The rate the payslip shows: only where both figures are known.
                        if (o.Phase.TaxableAmount is { } both && o.Phase.GrossAmount is { } gross)
                        {
                            lines.AddRange(TaxLineCodes.Split(TaxLineCodes.SalaryRateGross, item, gross.Amount));
                            lines.AddRange(TaxLineCodes.Split(TaxLineCodes.SalaryRateTaxable, item, both.Amount));
                        }
                    }
                    foreach (var f in salaryOneOffs.OrderBy(f => f.OccurredOn).ThenBy(f => f.Id))
                    {
                        var hasTaxable = taxableOf.TryGetValue(f.Id, out var oneOffTaxable);
                        lines.AddRange(TaxLineCodes.Split(TaxLineCodes.SalaryDetail, item, hasTaxable ? oneOffTaxable : f.Amount.Amount)
                            .Select(l => l with { On = f.OccurredOn, Note = f.Note, Category = hasTaxable ? TaxLineCodes.DetailEntered : TaxLineCodes.DetailNet }));
                    }
                    break;
                case IncomeKind.Unemployment:
                    lines.AddRange(TaxLineCodes.Split(TaxLineCodes.OtherTaxable, item, net + extra));
                    break;
                case IncomeKind.FamilyAllowance or IncomeKind.SocialBenefit:
                    lines.Add(new TaxLine(TaxLineCodes.NonTaxable, item.Id, item.DisplayName, null, net + extra));
                    break;
                default:
                    lines.Add(new TaxLine(TaxLineCodes.Unsupported, item.Id, item.DisplayName, null, net + extra));
                    break;
            }
        }
        return Result.Success<IReadOnlyList<TaxLine>>(lines);
    }
}
