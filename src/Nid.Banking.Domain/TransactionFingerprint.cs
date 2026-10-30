using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NodaTime;
using NodaTime.Text;

namespace Nid.Banking.Domain;

/// <summary>Spec §2.5: the identity of a bank line that has no id of its own, or whose id changes between reads.</summary>
public static class TransactionFingerprint
{
    public static string Compute(LocalDate bookedOn, decimal amount, string normalizedLabel, int rank)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{LocalDatePattern.Iso.Format(bookedOn)}|{amount.ToString("F2", CultureInfo.InvariantCulture)}|{normalizedLabel}|{rank}")));

    /// <summary>The fingerprint of each line, ranks counted in the given order.</summary>
    public static IReadOnlyList<string> ForBatch(IReadOnlyList<(LocalDate BookedOn, decimal Amount, string NormalizedLabel)> lines)
    {
        var ranks = new Dictionary<(LocalDate, string, string), int>();
        var result = new List<string>(lines.Count);
        foreach (var (bookedOn, amount, label) in lines)
        {
            var key = (bookedOn, amount.ToString("F2", CultureInfo.InvariantCulture), label);
            var rank = ranks[key] = ranks.GetValueOrDefault(key) + 1;
            result.Add(Compute(bookedOn, amount, label, rank));
        }

        return result;
    }
}
