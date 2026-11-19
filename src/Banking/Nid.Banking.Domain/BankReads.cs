using Nid.Banking.Contracts;
using NodaTime;

namespace Nid.Banking.Domain;

/// <summary>
/// #365: what Nid knows of a bank's read limit — its own count, never the bank's quota. PSD2 lets a bank refuse unattended reads past
/// <see cref="UnattendedLimit"/> a day per account; banks count their own way, so this is an estimate.
/// </summary>
/// <param name="Unattended">The account's reads made without the person over the last <see cref="BankReads.Window"/> (refusals aside).</param>
/// <param name="Attended">Those made while the person was there (a click on « Synchroniser » forwards their browser), which PSD2 does not limit.</param>
/// <param name="FreesAt">When the oldest unattended read of the window stops counting.</param>
/// <param name="LimitedAt">The latest refusal of the bank for a read limit, and whether the person was there.</param>
public sealed record ReadCount(int Unattended, int Attended, Instant? FreesAt, Instant? LimitedAt, bool LimitedAttended)
{
    public static readonly ReadCount None = new(0, 0, null, null, false);
}

public static class BankReads
{
    public const int UnattendedLimit = 4;

    public static readonly Duration Window = Duration.FromHours(24);

    /// <summary>How long the reads are kept: a week shows the last refusal past the day's window.</summary>
    public static readonly Duration Kept = Duration.FromDays(7);

    public static ReadCount Count(IEnumerable<BankRead> reads, Instant now)
    {
        var all = reads.ToList();
        var counted = all.Where(r => !r.RateLimited && r.At > now - Window).ToList();
        var unattended = counted.Where(r => !r.Attended).OrderBy(r => r.At).ToList();
        var limited = all.Where(r => r.RateLimited).MaxBy(r => r.At);
        return new ReadCount(unattended.Count, counted.Count - unattended.Count, unattended.Count > 0 ? unattended[0].At + Window : null,
            limited?.At, limited?.Attended ?? false);
    }

    /// <summary>Several accounts' counts as one (a connection's, the installation's): the busiest account, the latest refusal.</summary>
    public static ReadCount Worst(IEnumerable<ReadCount> counts)
    {
        var list = counts.ToList();
        if (list.Count == 0)
            return ReadCount.None;
        var busiest = list.OrderByDescending(c => c.Unattended).ThenBy(c => c.FreesAt).First();
        var limited = list.Where(c => c.LimitedAt is not null).MaxBy(c => c.LimitedAt);
        return busiest with { Attended = list.Max(c => c.Attended), LimitedAt = limited?.LimitedAt, LimitedAttended = limited?.LimitedAttended ?? false };
    }
}
