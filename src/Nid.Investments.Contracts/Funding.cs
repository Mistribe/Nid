using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Investments.Contracts;

/// <summary>
/// Savings goals spec §4: what Goals reads of an envelope. <see cref="Value"/> is today's value (the estimate when it is newer,
/// as the dashboard's), null without a valuation; <see cref="NetContributions12Months"/> is openings and contributions minus
/// withdrawals dated in the twelve months up to today, in the envelope's currency.
/// </summary>
public sealed record EnvelopeFundingFigures(
    ItemId Envelope, string Name, bool Closed, Money? Value, LocalDate? AsOf, bool Stale, bool Estimated, Money NetContributions12Months);

public interface IEnvelopeFunding
{
    /// <summary>
    /// The figures of the <paramref name="envelopes"/> the <paramref name="reader"/> can see in <paramref name="scope"/>, closed ones
    /// included; an id that is unknown, not an envelope or not visible is left out.
    /// </summary>
    Task<Result<IReadOnlyList<EnvelopeFundingFigures>>> ReadAsync(
        PersonId reader, VisibilityScope scope, IReadOnlyList<ItemId> envelopes, LocalDate today, CancellationToken ct);
}
