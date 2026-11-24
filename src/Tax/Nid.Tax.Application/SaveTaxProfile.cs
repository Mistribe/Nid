using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using Nid.Tax.Domain;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Tax.Application;

/// <summary>Tax estimate spec §7: the household's declarations for the year, then the estimate they give.</summary>
public sealed record SaveTaxProfileCommand(int Year, TaxProfile Profile, VisibilityScope Scope) : ICommand<TaxEstimateView>;

[RequestHandler<SaveTaxProfileCommand, TaxEstimateView>]
public sealed class SaveTaxProfileHandler(TaxEstimates estimates, ITaxProfileStore store, IHouseholdCalendar calendar)
    : IRequestHandler<SaveTaxProfileCommand, TaxEstimateView>
{
    public async ValueTask<Result<TaxEstimateView>> HandleAsync(SaveTaxProfileCommand command, CancellationToken ct = default)
    {
        var today = await calendar.TodayAsync(ct);
        if ((TaxEstimates.CheckYear(command.Year, today) ?? TaxEstimates.CheckProfile(command.Profile)) is { } failure)
            return Result.Failure<TaxEstimateView>(failure);
        await store.SaveAsync(command.Year, command.Profile, ct);
        return await estimates.EstimateAsync(command.Year, command.Scope, today, ct);
    }
}
