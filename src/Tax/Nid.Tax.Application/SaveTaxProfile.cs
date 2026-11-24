using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using Nid.Tax.Domain;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Tax.Application;

/// <summary>
/// Tax estimate spec §7: the household's declarations for the year, then the estimate they give. <c>Version</c> is the estimate's
/// <c>ProfileVersion</c> as the client read it: null when the year had no profile of its own.
/// </summary>
public sealed record SaveTaxProfileCommand(int Year, TaxProfile Profile, VisibilityScope Scope, uint? Version = null) : ICommand<TaxEstimateView>;

[RequestHandler<SaveTaxProfileCommand, TaxEstimateView>]
public sealed class SaveTaxProfileHandler(TaxEstimates estimates, ITaxProfileStore store, IHouseholdCalendar calendar)
    : IRequestHandler<SaveTaxProfileCommand, TaxEstimateView>
{
    public async ValueTask<Result<TaxEstimateView>> HandleAsync(SaveTaxProfileCommand command, CancellationToken ct = default)
    {
        var today = await calendar.TodayAsync(ct);
        if ((TaxEstimates.CheckYear(command.Year, today) ?? TaxEstimates.CheckProfile(command.Profile)) is { } failure)
            return Result.Failure<TaxEstimateView>(failure);
        if (!await store.SaveAsync(command.Year, command.Profile, command.Version, ct))
            return Result.Failure<TaxEstimateView>(new ConflictFailure("The year's tax profile changed since it was read: reload it.") { Code = TaxCodes.ProfileConflict });
        return await estimates.EstimateAsync(command.Year, command.Scope, today, ct);
    }
}
