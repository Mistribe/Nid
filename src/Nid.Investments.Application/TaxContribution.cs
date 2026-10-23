using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Investments.Application;

/// <summary>
/// Tax estimate spec §6: a brokerage account's income of the year (flat tax), a PER's contributions (deducted, per owner), its
/// opening too when it was opened that year, and the withdrawals from tax wrappers, whose tax is not estimated. Savings accounts
/// are taken as tax-free regulated ones.
/// </summary>
public sealed class EnvelopesTax(IVisibleItems visible, IItemRegistry items, IEnvelopeStore envelopes, IFlows flows) : ITaxContribution
{
    public async Task<Result<IReadOnlyList<TaxLine>>> TaxLinesAsync(PersonId reader, VisibilityScope scope, int year, LocalDate today, CancellationToken ct)
    {
        var found = (await envelopes.FindManyAsync((await visible.ForAsync(reader, scope, includeArchived: true, ct)).ToList(), ct))
            .Where(e => e.Kind is EnvelopeKind.Brokerage or EnvelopeKind.Per or EnvelopeKind.Pea or EnvelopeKind.LifeInsurance)
            .ToList();
        var ids = found.Select(e => e.ItemId).ToList();
        var accesses = await items.FindManyAsync(ids, ct);
        var read = await flows.ListManyAsync(ids, TaxLineCodes.Year(year), new HashSet<FlowKind> { FlowKind.Income, FlowKind.Contribution, FlowKind.Opening, FlowKind.Withdrawal }, ct);
        if (read.TryGetFailure(out var failure)) return Result.Failure<IReadOnlyList<TaxLine>>(failure!);
        read.TryGetValue(out var byItem);

        var lines = new List<TaxLine>();
        foreach (var envelope in found)
        {
            if (!accesses.TryGetValue(envelope.ItemId, out var item) || !TaxLineCodes.InEuros(item, lines))
                continue;
            var facts = byItem!.GetValueOrDefault(envelope.ItemId, []);
            decimal Sum(FlowKind kind) => facts.Where(f => f.Kind == kind).Sum(f => f.Amount.Amount);
            if (envelope.Kind == EnvelopeKind.Brokerage && Sum(FlowKind.Income) is > 0 and var income)
                lines.Add(new TaxLine(TaxLineCodes.CapitalIncome, item.Id, item.DisplayName, null, income));
            // An opening is the payments made before the tracking: paid in the year only when the PER was opened in it.
            var opening = envelope.OpenedOn?.Year == year ? Sum(FlowKind.Opening) : 0m;
            if (envelope.Kind == EnvelopeKind.Per && Sum(FlowKind.Contribution) + opening is > 0 and var paid)
                lines.AddRange(TaxLineCodes.Split(TaxLineCodes.PerContribution, item, paid));
            if (envelope.Kind != EnvelopeKind.Brokerage && Sum(FlowKind.Withdrawal) is > 0 and var withdrawn)
                lines.Add(new TaxLine(TaxLineCodes.Withdrawal, item.Id, item.DisplayName, null, withdrawn));
        }
        return Result.Success<IReadOnlyList<TaxLine>>(lines);
    }
}
