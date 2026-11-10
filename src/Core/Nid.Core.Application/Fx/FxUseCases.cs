using System.Globalization;
using Nid.Core.Contracts;
using Nid.Core.Domain.Fx;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Fx;

public static class FxLimits
{
    public const int MaxRangeDays = 400;
}

public sealed record CollectDailyFxRatesCommand : ICommand<FxSyncRunView>, IAdminOnly;

public sealed record BackfillFxRatesCommand : ICommand<FxSyncRunView>, IAdminOnly;

public sealed record RequestFxSyncCommand : ICommand<FxSyncRequested>, IAdminOnly;

public sealed record SetManualFxRateCommand(LocalDate OnDate, string Quote, string Rate) : ICommand<FxRateView>, IAdminOnly;

public sealed record GetFxRatesQuery(string Quote, LocalDate From, LocalDate To) : IQuery<IReadOnlyList<FxRateView>>;

public sealed record GetFxSyncRunsQuery(int? Page) : IQuery<FxSyncRunPage>, IAdminOnly;

[RequestHandler<CollectDailyFxRatesCommand, FxSyncRunView>]
public sealed class CollectDailyFxRatesHandler(FxCollector collector) : IRequestHandler<CollectDailyFxRatesCommand, FxSyncRunView>
{
    public async ValueTask<Result<FxSyncRunView>> HandleAsync(CollectDailyFxRatesCommand command, CancellationToken ct = default)
        => (await collector.CollectDailyAsync(ct)).Map(FxSyncRunView.From);
}

[RequestHandler<BackfillFxRatesCommand, FxSyncRunView>]
public sealed class BackfillFxRatesHandler(FxCollector collector) : IRequestHandler<BackfillFxRatesCommand, FxSyncRunView>
{
    public async ValueTask<Result<FxSyncRunView>> HandleAsync(BackfillFxRatesCommand command, CancellationToken ct = default)
        => (await collector.BackfillAsync(ct)).Map(FxSyncRunView.From);
}

[RequestHandler<RequestFxSyncCommand, FxSyncRequested>]
public sealed class RequestFxSyncHandler(IFxRunLog runs, IFxJobTrigger trigger) : IRequestHandler<RequestFxSyncCommand, FxSyncRequested>
{
    public async ValueTask<Result<FxSyncRequested>> HandleAsync(RequestFxSyncCommand command, CancellationToken ct = default)
    {
        if (await runs.IsRunningAsync(ct))
            return Result.Failure<FxSyncRequested>(new Failure(FxCodes.SyncRunning, "A rate collection is already running.", null));

        var triggered = await trigger.TriggerBackfillAsync(ct);
        return triggered.TryGetFailure(out var failure)
            ? Result.Failure<FxSyncRequested>(failure!)
            : Result.Success(new FxSyncRequested(true));
    }
}

[RequestHandler<SetManualFxRateCommand, FxRateView>]
public sealed class SetManualFxRateHandler(IFxRateStore rates, IClock clock) : IRequestHandler<SetManualFxRateCommand, FxRateView>
{
    public async ValueTask<Result<FxRateView>> HandleAsync(SetManualFxRateCommand command, CancellationToken ct = default)
    {
        if (!Currency.TryCreate(command.Quote, out var quote) || quote == Currency.Usd)
            return Invalid("The quote is an ISO 4217 code other than USD, whose rate is always 1.");
        if (!decimal.TryParse(command.Rate, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate)
            || rate <= 0m || rate > FxRebase.MaxRate || decimal.Round(rate, 10) != rate)
            return Invalid("The rate is a positive decimal string with at most nine integer digits and ten decimals.");
        if (command.OnDate > clock.GetCurrentInstant().InUtc().Date)
            return Invalid("A rate cannot be entered for a future date.");

        var stored = new FxRate(command.OnDate, quote, rate, FxRateSource.Manual);
        await rates.WriteAsync([stored], ct);
        return Result.Success(FxRateView.From(stored));
    }

    private static Result<FxRateView> Invalid(string message)
        => Result.Failure<FxRateView>(new Failure(FxCodes.InvalidRate, message, null));
}

[RequestHandler<GetFxRatesQuery, IReadOnlyList<FxRateView>>]
public sealed class GetFxRatesHandler(IFxRateStore rates) : IRequestHandler<GetFxRatesQuery, IReadOnlyList<FxRateView>>
{
    public async ValueTask<Result<IReadOnlyList<FxRateView>>> HandleAsync(GetFxRatesQuery query, CancellationToken ct = default)
    {
        if (!Currency.TryCreate(query.Quote, out var quote))
            return Invalid("The quote is an ISO 4217 code.");
        if (query.To < query.From || Period.DaysBetween(query.From, query.To) >= FxLimits.MaxRangeDays)
            return Invalid($"The range runs forward and spans at most {FxLimits.MaxRangeDays} days.");

        var stored = await rates.ReadAsync([quote], query.From, query.To, ct);
        return Result.Success<IReadOnlyList<FxRateView>>(stored.Select(FxRateView.From).ToList());
    }

    private static Result<IReadOnlyList<FxRateView>> Invalid(string message)
        => Result.Failure<IReadOnlyList<FxRateView>>(new Failure(FxCodes.InvalidRange, message, null));
}

[RequestHandler<GetFxSyncRunsQuery, FxSyncRunPage>]
public sealed class GetFxSyncRunsHandler(IFxRunLog runs) : IRequestHandler<GetFxSyncRunsQuery, FxSyncRunPage>
{
    public async ValueTask<Result<FxSyncRunPage>> HandleAsync(GetFxSyncRunsQuery query, CancellationToken ct = default)
    {
        var (page, skip) = ListPage.Of(query.Page, FxSyncRunPage.PageSize);
        var (items, total) = await runs.PageAsync(skip, FxSyncRunPage.PageSize, ct);
        return Result.Success(new FxSyncRunPage(items.Select(FxSyncRunView.From).ToList(), total, page));
    }
}
