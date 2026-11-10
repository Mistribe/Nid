using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Investments.Contracts;

/// <summary>
/// Savings goals spec §4: what Goals reads of an envelope on a date. <see cref="Value"/> is its value then (today, the estimate
/// when it is newer, as the dashboard's; before today, the latest valuation on or before the date), null without a valuation;
/// <see cref="NetContributions12Months"/> is openings and contributions minus withdrawals dated in the twelve months up to the date,
/// in the envelope's currency. <see cref="Closed"/>: closed on or before the date (today, archived).
/// </summary>
public sealed record EnvelopeFundingFigures(
    ItemId Envelope, string Name, bool Closed, Money? Value, LocalDate? AsOf, bool Stale, bool Estimated, Money NetContributions12Months);

public interface IEnvelopeFunding
{
    /// <summary>
    /// The figures of the <paramref name="envelopes"/> the <paramref name="reader"/> can see in <paramref name="scope"/>, closed ones
    /// included; an id that is unknown, not an envelope or not visible is left out. Read on <paramref name="date"/>, today or before.
    /// </summary>
    Task<Result<IReadOnlyList<EnvelopeFundingFigures>>> ReadAsync(
        PersonId reader, VisibilityScope scope, IReadOnlyList<ItemId> envelopes, LocalDate date, CancellationToken ct);
}
