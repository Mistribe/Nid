using Nid.Banking.Contracts;
using NodaTime;

namespace Nid.Banking.Domain;

/// <summary>Issue #81: the shortfall ahead, the first day a checking account's balance less its charges to come goes below the margin.</summary>
public static class Shortfalls
{
    /// <summary>A balance older than this says nothing of today.</summary>
    public const int FreshDays = 7;

    /// <summary>The window when no income is expected.</summary>
    public const int WindowDays = 31;

    /// <param name="Account">The account its item's last debit line was on; null when none was matched.</param>
    public sealed record Due(LocalDate On, decimal Amount, string Name, Guid? Account);

    /// <param name="Charges">The charges counted up to and including <paramref name="On"/>'s.</param>
    /// <param name="Names">The charges due on <paramref name="On"/>, the ones that tipped it over.</param>
    public sealed record Shortfall(Guid Account, decimal Balance, LocalDate On, decimal Charges, IReadOnlyList<string> Names);

    public static bool Fresh(BankBalance? latest, LocalDate today) => latest is not null && latest.AsOf >= today.PlusDays(-FreshDays);

    /// <summary>The window's end, exclusive: the next income's day, else <see cref="WindowDays"/> days on.</summary>
    public static LocalDate End(LocalDate today, LocalDate? nextIncome) => nextIncome ?? today.PlusDays(WindowDays);

    /// <summary>
    /// Per account with a fresh balance, the first day in [<paramref name="today"/>, <paramref name="end"/>) its balance less the charges
    /// due so far goes below <paramref name="margin"/>. An unmatched charge counts only when <paramref name="accounts"/> holds a single account.
    /// </summary>
    public static IEnumerable<Shortfall> Find(
        LocalDate today, LocalDate end, decimal margin, IReadOnlyDictionary<Guid, BankBalance?> accounts, IEnumerable<Due> dues)
    {
        var inWindow = dues.Where(d => d.On >= today && d.On < end).ToList();
        foreach (var (id, latest) in accounts)
        {
            if (!Fresh(latest, today))
                continue;
            var total = 0m;
            foreach (var day in inWindow.Where(d => d.Account == id || (d.Account is null && accounts.Count == 1)).GroupBy(d => d.On).OrderBy(g => g.Key))
            {
                total += day.Sum(d => d.Amount);
                if (latest!.Amount - total < margin)
                {
                    yield return new Shortfall(id, latest.Amount, day.Key, total, [.. day.Select(d => d.Name).Distinct()]);
                    break;
                }
            }
        }
    }
}
