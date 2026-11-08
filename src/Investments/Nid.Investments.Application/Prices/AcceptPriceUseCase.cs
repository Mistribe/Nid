using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Investments.Application.Prices;

/// <summary>
/// « Accepter ce cours » (holding prices spec §6): the envelope's write right is the check, prices belong to no item (D11),
/// and the trace is <c>accepted_by</c> and <c>accepted_at</c> on the price row.
/// </summary>
public sealed record AcceptHoldingPriceCommand(ItemId ItemId, Guid Holding, LocalDate OnDate) : ICommand<bool>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

[RequestHandler<AcceptHoldingPriceCommand, bool>]
public sealed class AcceptHoldingPriceHandler(
    IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings, ISecurityPriceStore prices, IActorAccessor actor, IClock clock)
    : IRequestHandler<AcceptHoldingPriceCommand, bool>
{
    public async ValueTask<Result<bool>> HandleAsync(AcceptHoldingPriceCommand command, CancellationToken ct = default)
    {
        if ((await HoldingCheck.OpenAsync(envelopes, items, command.ItemId, ct)).TryGetFailure(out var failure))
            return Result.Failure<bool>(failure!);
        var isin = (await holdings.HoldingsAsync(command.ItemId, ct)).FirstOrDefault(h => h.Holding.Id == command.Holding)?.Holding.Isin;
        if (isin is null)
            return Result.Failure<bool>(new NotFoundFailure("holding", command.Holding.ToString()));

        return await prices.AcceptAsync(isin, command.OnDate, actor.Current!.PersonId!.Value, clock.GetCurrentInstant(), ct)
            ? Result.Success(true)
            : Result.Failure<bool>(new Failure(PriceCodes.NotRejected, "This holding has no rejected price on that day.", null));
    }
}
