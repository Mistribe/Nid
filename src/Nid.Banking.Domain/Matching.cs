using NodaTime;

namespace Nid.Banking.Domain;

/// <summary>A bank line as the matching rules read it: <see cref="Amount"/> signed (debit negative), <see cref="Stem"/> the label stem.</summary>
public sealed record Line(Guid Id, Guid Account, LocalDate BookedOn, decimal Amount, string Currency, string Stem);

/// <summary>An expected charge: <see cref="Amount"/> positive, <see cref="Credit"/> gives the direction.</summary>
public sealed record Due(int Index, LocalDate DueOn, decimal Amount, bool Credit, string Currency, int ToleranceDays);

/// <summary>Spec §6.3: which expected charges a line may be.</summary>
public static class ChargeMatch
{
    public const decimal ForeignTolerance = 0.03m;

    private static int Gap(Line line, Due charge) => Math.Abs(Period.Between(charge.DueOn, line.BookedOn, PeriodUnits.Days).Days);

    /// <summary>Indexes of the charges of the line's direction within tolerance, nearest date first.</summary>
    public static IReadOnlyList<int> Near(Line line, IReadOnlyList<Due> charges)
        => charges
            .Where(c => c.Credit == line.Amount > 0 && Gap(line, c) <= c.ToleranceDays)
            .OrderBy(c => Gap(line, c)).ThenBy(c => c.Index)
            .Select(c => c.Index)
            .ToList();

    /// <summary>true: as expected; false: differs; null: foreign charge without a converted amount.</summary>
    public static bool? Agrees(Line line, Due charge, decimal? convertedToLineCurrency)
    {
        var amount = Math.Abs(line.Amount);
        if (string.Equals(line.Currency, charge.Currency, StringComparison.Ordinal))
            return Math.Round(amount, 2) == Math.Round(charge.Amount, 2);
        return convertedToLineCurrency is { } converted
            ? Math.Abs(amount - converted) <= converted * ForeignTolerance
            : null;
    }

    /// <summary>The single near charge that agrees, or null (none, or two or more).</summary>
    public static int? SingleCandidate(Line line, IReadOnlyList<Due> charges, Func<Due, decimal?> converted)
    {
        var byIndex = charges.ToDictionary(c => c.Index);
        var agreeing = Near(line, charges).Where(i => ChargeMatch.Agrees(line, byIndex[i], converted(byIndex[i])) == true).Take(2).ToList();
        return agreeing.Count == 1 ? agreeing[0] : null;
    }
}

/// <summary>Spec §6.3 step 1: a debit and a credit on two accounts are one transfer.</summary>
public static class InternalTransfers
{
    public const int Days = 3;

    private static int Gap(Line a, Line b) => Math.Abs(Period.Between(a.BookedOn, b.BookedOn, PeriodUnits.Days).Days);

    /// <summary>The pairs, nearest first; two accounts pair only when <paramref name="pairable"/> says so (all, when null).</summary>
    public static IReadOnlyList<(Guid Debit, Guid Credit)> Pair(IReadOnlyList<Line> lines, Func<Guid, Guid, bool>? pairable = null)
    {
        var debits = lines.Where(l => l.Amount < 0).OrderBy(l => l.BookedOn).ThenBy(l => l.Id).ToList();
        var credits = lines.Where(l => l.Amount > 0).OrderBy(l => l.BookedOn).ThenBy(l => l.Id).ToList();
        var candidates = (
            from d in debits
            from c in credits
            where d.Account != c.Account
                && string.Equals(d.Currency, c.Currency, StringComparison.Ordinal)
                && Math.Round(-d.Amount, 2) == Math.Round(c.Amount, 2)
                && Gap(d, c) <= Days
                && (pairable?.Invoke(d.Account, c.Account) ?? true)
            select (Debit: d, Credit: c, Days: Gap(d, c)))
            .OrderBy(p => p.Days) // stable: ties keep the debit, then credit, booking order
            .ToList();

        var used = new HashSet<Guid>();
        var pairs = new List<(Guid, Guid)>();
        foreach (var (debit, credit, _) in candidates)
            if (!used.Contains(debit.Id) && !used.Contains(credit.Id))
            {
                used.Add(debit.Id);
                used.Add(credit.Id);
                pairs.Add((debit.Id, credit.Id));
            }
        return pairs;
    }
}

/// <summary>Spec §6.3 step 2: a checking-account debit that pays a card's lines since its previous settlement.</summary>
public static class CardSettlement
{
    public const int FirstWindowDays = 45;

    public static bool Settles(Line debit, IReadOnlyList<Line> cardLines, LocalDate since)
    {
        var inWindow = cardLines.Where(l => l.BookedOn >= since && l.BookedOn < debit.BookedOn).ToList();
        return inWindow.Count > 0 && Math.Round(inWindow.Sum(l => l.Amount), 2) == Math.Round(debit.Amount, 2);
    }
}
