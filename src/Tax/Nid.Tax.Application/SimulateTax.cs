using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using Nid.Tax.Domain;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Tax.Application;

/// <summary>Tax estimate spec §8: the calculation of given inputs; nothing is read nor stored.</summary>
public sealed record SimulateTaxQuery(TaxInputs Inputs, VisibilityScope? Scope = null) : IQuery<TaxResultView>;

[RequestHandler<SimulateTaxQuery, TaxResultView>]
public sealed class SimulateTaxHandler(IHouseholdCalendar calendar, TaxEstimates estimates) : IRequestHandler<SimulateTaxQuery, TaxResultView>
{
    public async ValueTask<Result<TaxResultView>> HandleAsync(SimulateTaxQuery query, CancellationToken ct = default)
    {
        var inputs = query.Inputs;
        var today = await calendar.TodayAsync(ct);
        if ((TaxEstimates.CheckYear(inputs.Year, today) ?? TaxEstimates.CheckInputs(inputs)) is { } failure)
            return Result.Failure<TaxResultView>(failure);
        var simulated = TaxCalculator.Compute(inputs);
        var view = TaxResultView.Of(simulated);
        if (query.Scope is not { } scope) return Result.Success(view);
        var estimate = await estimates.ComputeAsync(inputs.Year, scope, today, ct);
        if (estimate.TryGetFailure(out var estimateFailure)) return Result.Failure<TaxResultView>(estimateFailure!);
        estimate.TryGetValue(out var before);
        return Result.Success(view with
        {
            Differences = new TaxDifferencesView(
                TaxEstimates.Amount(simulated.TotalTax - before!.TotalTax), TaxEstimates.Amount(simulated.NetAfterTaxMonthly - before.NetAfterTaxMonthly),
                TaxEstimates.Amount(simulated.Withholding.Balance - before.Withholding.Balance)),
        });
    }
}
