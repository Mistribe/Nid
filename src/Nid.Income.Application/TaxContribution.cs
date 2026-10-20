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
public sealed class IncomeSourcesTax(IVisibleItems visible, IItemRegistry items, ISourceStore sources, IPhaseStore phases, IFlows flows) : ITaxContribution
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

        var lines = new List<TaxLine>();
        foreach (var source in found)
        {
            if (!accesses.TryGetValue(source.ItemId, out var item) || !TaxLineCodes.InEuros(item, lines))
                continue;
            var occurrences = bySource[source.ItemId].SelectMany(p => IncomeMath.Occurrences(p, from, until).Select(o => (Phase: p, o.Amount))).ToList();
            var extra = byItem!.GetValueOrDefault(source.ItemId, []).Where(f => f.Category == IncomeWire.OneOffCategory).Sum(f => f.Amount.Amount);
            var net = occurrences.Sum(o => o.Amount.Amount);
            switch (source.Kind)
            {
                case IncomeKind.Salary:
                    lines.AddRange(TaxLineCodes.Split(TaxLineCodes.SalaryGross, item,
                        occurrences.Where(o => o.Phase.GrossAmount is not null).Sum(o => o.Phase.GrossAmount!.Value.Amount)));
                    var withoutGross = occurrences.Where(o => o.Phase.GrossAmount is null).Sum(o => o.Amount.Amount);
                    if (withoutGross > 0)
                        lines.AddRange(TaxLineCodes.Split(TaxLineCodes.SalaryNet, item, withoutGross));
                    if (extra > 0)
                        lines.AddRange(TaxLineCodes.Split(TaxLineCodes.SalaryOneOff, item, extra));
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
