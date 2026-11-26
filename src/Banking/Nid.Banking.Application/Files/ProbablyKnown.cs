using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using NodaTime;

namespace Nid.Banking.Application.Files;

public static class ProbablyKnown
{
    /// <summary>
    /// D23, on an account <paramref name="other"/> (<c>api</c> or <c>file</c>) also fills: of <paramref name="lines"/>, those whose fingerprint
    /// and ref are unknown, held back by a stored line of <paramref name="other"/> of the same amount booked within a day that none of
    /// <paramref name="lines"/> is exactly. One to one: a stored line holds back one line at most, the same day's first. A file after the API
    /// (D23) and the API after a file (#779) write the same operation under two labels.
    /// </summary>
    public static async Task<bool[]> ForAsync(ITransactionStore transactions, Guid account, IReadOnlyList<NewBankTransaction> lines, string other, CancellationToken ct)
    {
        var probable = new bool[lines.Count];
        if (!await transactions.HasFetchedViaAsync(account, other, ct))
            return probable;

        var near = await transactions.NearAsync(account, lines.Select(l => (l.BookedOn, l.Amount)).ToList(), ct);
        var fingerprints = TransactionFingerprint.ForBatch(lines.Select(l => (l.BookedOn, l.Amount, TransactionLabel.Normalize(l.Label))).ToList());
        var ownFingerprints = fingerprints.ToHashSet();
        var ownRefs = lines.Select(l => l.ProviderRef).OfType<string>().ToHashSet();
        var storedFingerprints = near.Select(t => t.Fingerprint).ToHashSet();
        var storedRefs = near.Select(t => t.ProviderRef).ToHashSet();
        var twins = near.Where(t => t.FetchedVia == other && !ownFingerprints.Contains(t.Fingerprint) && !ownRefs.Contains(t.ProviderRef)).ToList();
        // The unknown lines by (amount, day), in their order.
        var open = Enumerable.Range(0, lines.Count)
            .Where(i => !storedFingerprints.Contains(fingerprints[i]) && !(lines[i].ProviderRef is { } r && storedRefs.Contains(r)))
            .GroupBy(i => (lines[i].Amount, lines[i].BookedOn))
            .ToDictionary(g => g.Key, g => new Queue<int>(g));
        var unmatched = twins.Where(t => !Take(t.Amount, t.BookedOn)).ToList();
        foreach (var t in unmatched)
            _ = Take(t.Amount, t.BookedOn.PlusDays(-1)) || Take(t.Amount, t.BookedOn.PlusDays(1));
        return probable;

        bool Take(decimal amount, LocalDate on)
        {
            if (!open.TryGetValue((amount, on), out var waiting) || !waiting.TryDequeue(out var i))
                return false;
            probable[i] = true;
            return true;
        }
    }
}
