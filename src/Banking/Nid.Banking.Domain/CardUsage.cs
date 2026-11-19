using NodaTime;

namespace Nid.Banking.Domain;

/// <summary>
/// #362: a card's « minimum usage » rule — some banks charge a fee for a month without a card payment — and the payments that count
/// towards it. The household sets the rule: Nid cannot know each bank's conditions.
/// </summary>
public static class CardUsage
{
    public const int MaxPayments = 31;

    /// <summary>How old a card's last read may be before Nid stops concluding « unused ».</summary>
    public static readonly Duration Fresh = Duration.FromDays(2);

    // A cash withdrawal's usual words, normalised (upper case, no accent), matched as whole words: « DABOVILLE » is a payment.
    private static readonly HashSet<string> Withdrawal = ["RETRAIT", "RETRAITS", "DAB", "GAB", "ATM", "WITHDRAWAL"];

    /// <summary>
    /// The card's payments booked within [<paramref name="from"/>, <paramref name="to"/>]: its debits; a refund (a credit) never counts, a
    /// withdrawal only when <paramref name="countWithdrawals"/>.
    /// </summary>
    public static int Payments(IEnumerable<(LocalDate BookedOn, decimal Amount, string NormalizedLabel)> lines, LocalDate from, LocalDate to, bool countWithdrawals)
        => lines.Count(l => l.BookedOn >= from && l.BookedOn <= to && l.Amount < 0m
            && (countWithdrawals || !l.NormalizedLabel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(Withdrawal.Contains)));

    /// <summary>Whether <paramref name="today"/> is within the last <paramref name="days"/> days of its month (its last day counting as one).</summary>
    public static bool Due(LocalDate today, int days)
        => Period.DaysBetween(today, today.With(DateAdjusters.EndOfMonth)) < days;
}
