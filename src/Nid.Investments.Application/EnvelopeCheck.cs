using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Investments.Application;

internal static class EnvelopeCheck
{
    /// <summary>Every current fact, whenever dated: the range <see cref="EnvelopePerformance"/> and the close/transfer checks read over.</summary>
    public static readonly DateRange Everything = DateRange.Create(LocalDate.MinIsoValue, null).TryGetValue(out var r) ? r : throw new InvalidOperationException();

    /// <summary>The envelope and its item; an item that is not an envelope answers as a missing envelope.</summary>
    public static async Task<Result<(Envelope Envelope, ItemAccess Item)>> LoadAsync(
        IEnvelopeStore envelopes, IItemRegistry items, ItemId id, CancellationToken ct)
    {
        var envelope = await envelopes.FindAsync(id, ct);
        var item = envelope is null ? null : await items.FindAsync(id, ct);
        return envelope is null || item is null
            ? Result.Failure<(Envelope, ItemAccess)>(new NotFoundFailure("envelope", id.ToString()))
            : Result.Success((envelope, item));
    }

    public static Result RequireOpen(ItemAccess item)
        => item.ArchivedAt is null ? Result.Success()
            : Result.Failure(new Failure(InvestmentsCodes.EnvelopeClosed, "This envelope is closed: reopen it first.", null));
}
