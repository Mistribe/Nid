using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Investments.Application.Prices;

/// <summary>Sent by the <c>prices-daily</c> job as the system actor, who is admin.</summary>
public sealed record CollectPricesCommand : ICommand<PriceRunView>, IAdminOnly;

public sealed record RequestPriceSyncCommand : ICommand<PriceSyncRequested>, IAdminOnly;

public sealed record GetPriceRunsQuery : IQuery<IReadOnlyList<PriceRunView>>, IAdminOnly;

/// <summary>A run as the admin page shows it: counts only, never an ISIN (spec D12).</summary>
public sealed record PriceRunView(
    Guid Id, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Status, int Queried, int Accepted, int Rejected, int NotFound, int SourceErrors,
    string? FailureCode)
{
    public static PriceRunView From(PriceRun run)
        => new(run.Id, run.StartedAt.ToDateTimeOffset(), run.FinishedAt?.ToDateTimeOffset(), run.Status.ToString().ToLowerInvariant(),
            run.Queried, run.Accepted, run.Rejected, run.NotFound, run.SourceErrors, run.FailureCode);
}

public sealed record PriceSyncRequested(bool Requested);

[RequestHandler<CollectPricesCommand, PriceRunView>]
public sealed class CollectPricesHandler(PriceCollector collector) : IRequestHandler<CollectPricesCommand, PriceRunView>
{
    public async ValueTask<Result<PriceRunView>> HandleAsync(CollectPricesCommand command, CancellationToken ct = default)
        => (await collector.CollectAsync(ct)).Map(PriceRunView.From);
}

[RequestHandler<RequestPriceSyncCommand, PriceSyncRequested>]
public sealed class RequestPriceSyncHandler(IPriceRunLog runs, IHouseholdSettings settings, IPriceJobTrigger trigger)
    : IRequestHandler<RequestPriceSyncCommand, PriceSyncRequested>
{
    public async ValueTask<Result<PriceSyncRequested>> HandleAsync(RequestPriceSyncCommand command, CancellationToken ct = default)
    {
        if (await runs.IsRunningAsync(ct))
            return Result.Failure<PriceSyncRequested>(new Failure(PriceCodes.SyncRunning, "A price collection is already running.", null));
        if (!(await settings.ReadAsync(ct)).PricesEnabled)
            return Result.Failure<PriceSyncRequested>(new Failure(PriceCodes.Disabled, "Price collection is turned off for this household.", null));

        var triggered = await trigger.TriggerAsync(ct);
        return triggered.TryGetFailure(out var failure)
            ? Result.Failure<PriceSyncRequested>(failure!)
            : Result.Success(new PriceSyncRequested(true));
    }
}

[RequestHandler<GetPriceRunsQuery, IReadOnlyList<PriceRunView>>]
public sealed class GetPriceRunsHandler(IPriceRunLog runs) : IRequestHandler<GetPriceRunsQuery, IReadOnlyList<PriceRunView>>
{
    public const int RecentRuns = 50;

    public async ValueTask<Result<IReadOnlyList<PriceRunView>>> HandleAsync(GetPriceRunsQuery query, CancellationToken ct = default)
        => Result.Success<IReadOnlyList<PriceRunView>>((await runs.RecentAsync(RecentRuns, ct)).Select(PriceRunView.From).ToList());
}
