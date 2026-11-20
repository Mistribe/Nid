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

/// <summary>
/// #416: an immediate-debit card's line and its echo, the same debit on the checking account the card is attached to within
/// <see cref="InternalTransfers.Days"/> days. One echo per card line and one card line per echo, nearest first; a line with two candidates
/// equally near (two identical purchases the same day) links to none of them.
/// </summary>
public static class CardEchoes
{
    public static IReadOnlyList<(Guid Echo, Guid CardLine)> Pair(IReadOnlyList<Line> checking, IReadOnlyList<Line> card)
    {
        var candidates = (
            from d in checking
            from c in card
            where d.Amount < 0 && c.Amount < 0
                && string.Equals(d.Currency, c.Currency, StringComparison.Ordinal)
                && Math.Round(d.Amount, 2) == Math.Round(c.Amount, 2)
                && Math.Abs(Period.DaysBetween(c.BookedOn, d.BookedOn)) <= InternalTransfers.Days
            select (Echo: d.Id, CardLine: c.Id, Days: Math.Abs(Period.DaysBetween(c.BookedOn, d.BookedOn))))
            .ToList();

        var used = new HashSet<Guid>();
        var pairs = new List<(Guid, Guid)>();
        foreach (var level in candidates.GroupBy(p => p.Days).OrderBy(g => g.Key))
        {
            var open = level.Where(p => !used.Contains(p.Echo) && !used.Contains(p.CardLine)).ToList();
            foreach (var (echo, cardLine, _) in open)
                if (open.Count(p => p.Echo == echo) == 1 && open.Count(p => p.CardLine == cardLine) == 1)
                    pairs.Add((echo, cardLine));
            // A tied line is out for good: a farther candidate is no surer than the two near ones.
            used.UnionWith(open.SelectMany(p => new[] { p.Echo, p.CardLine }));
        }
        return pairs;
    }
}

/// <summary>
/// #353: the checking account a card most likely belongs to, as a suggestion. The bank never says (Enable Banking has no such field):
/// the only checking account of the card's own connection, else the one whose lines echo the card's — the same debit within
/// <see cref="InternalTransfers.Days"/> days (immediate debit) or a debit paying the card's lines of the month or two before (deferred).
/// </summary>
public static class CardParents
{
    /// <param name="sameConnection">Whether a candidate is seen by a connection that sees the card: those are tried first.</param>
    public static Guid? Suggest(IReadOnlyList<Line> card, IReadOnlyList<(Guid Account, bool SameConnection, IReadOnlyList<Line> Lines)> candidates)
    {
        var pool = candidates.Any(c => c.SameConnection) ? candidates.Where(c => c.SameConnection).ToList() : candidates.ToList();
        if (pool.Count == 1)
            return pool[0].Account;
        var scores = pool.Select(c => (c.Account, Score: Score(card, c.Lines))).Where(s => s.Score > 0).OrderByDescending(s => s.Score).ToList();
        // A tie is no suggestion.
        return scores is [var best, ..] && (scores.Count == 1 || scores[1].Score < best.Score) ? best.Account : null;
    }

    private static int Score(IReadOnlyList<Line> card, IReadOnlyList<Line> checking)
    {
        var debits = checking.Where(l => l.Amount < 0).ToList();
        var mirrored = card.Count(c => c.Amount < 0
            && debits.Any(d => Math.Round(d.Amount, 2) == Math.Round(c.Amount, 2) && Math.Abs(Period.DaysBetween(c.BookedOn, d.BookedOn)) <= InternalTransfers.Days));
        // ponytail: a deferred card's cycle is taken as calendar months, the usual French one; another cycle scores by its mirrored lines only.
        var settled = debits.Count(d => card.Count > 0 && new[] { d.BookedOn.With(DateAdjusters.StartOfMonth), d.BookedOn.PlusMonths(-1).With(DateAdjusters.StartOfMonth) }
            .Any(since => CardSettlement.Settles(d, card, since)));
        // Either pattern, not both: a debit that echoes a single card line also « pays » it.
        return Math.Max(mirrored, settled);
    }
}
