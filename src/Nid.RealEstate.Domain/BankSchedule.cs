using Nid.RealEstate.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.RealEstate.Domain;

/// <summary>
/// Spec 3F2 §2 (loans.md §6): the bank's table against its loan — its rules, the schedule it makes as the reference, and the
/// comparison. A refusal names <c>rows[k].column</c> (<c>dueDate</c>, <c>payment</c>, <c>interest</c>, <c>principal</c>,
/// <c>insurance</c>, <c>crd</c>), k the row's rank in the list given, or <c>rows</c> for an empty table; an early repayment the
/// bank's balance cannot take answers at the engine's <c>events[i].amount</c>.
/// </summary>
public static class BankSchedule
{
    /// <summary>loans.md §6: a gap of 0.05 or less on a row is rounding.</summary>
    public const decimal Rounding = 0.05m;

    /// <summary>loans.md §6 and §14: the remaining balance diverges beyond this gap (still to be measured on real tables).</summary>
    public const decimal Divergence = 1m;

    /// <summary>The loans' bound on every amount (numeric(19,4) holds less than 10¹⁵), as <c>LoanRules.AmountBound</c>.</summary>
    private const decimal AmountBound = 1_000_000_000_000m;

    /// <summary>
    /// The schedule with the bank as the reference: the calculation's rows before the table, the bank's rows — an early repayment
    /// dated from a row's due date to the next lowers the balance after it, as the engine applies one — then the calculation from
    /// the last row's balance (<see cref="LoanScheduleBuilder.Continue"/>). The table's rules are checked first.
    /// </summary>
    public static Result<LoanSchedule> Merge(LoanTerms t, LoanSchedule computed, IReadOnlyList<BankRow> bank)
    {
        if (Check(t, computed, bank) is { } refused)
            return Result.Failure<LoanSchedule>(refused);
        var byDate = computed.Rows.ToDictionary(r => r.DueDate);
        var first = byDate[bank[0].DueDate].Number;
        var head = computed.Rows.Where(r => r.Number < first).ToList();
        foreach (var b in bank)
        {
            var c = byDate[b.DueDate];
            var early = Early(t, c.Number);
            head.Add(new ScheduleRow(c.Number, b.DueDate, c.PhaseSeq, c.AnnualRate, b.Payment, b.Interest, b.Principal,
                b.Insurance ?? c.Insurance, b.Crd - (early ?? 0m), early, FromBank: true));
        }
        var continued = LoanScheduleBuilder.Continue(t, head);
        if (!continued.TryGetValue(out var schedule))
            return continued;
        return Result.Success(new LoanSchedule(schedule!.Rows, [.. computed.Warnings.Where(w => w.Row < first), .. schedule.Warnings]));
    }

    /// <summary>
    /// Spec 3F2 §2's rules for a table, the first broken one, or null: at least one row; amounts of 0 or more, below 10¹², with two
    /// decimals at most; each date a due date of the computed schedule, the dates following each other without a missing one; the
    /// payment the interest plus the principal, but for a total deferral's row that pays nothing; the balance after early
    /// repayments never below 0, and never rising but under a total deferral.
    /// </summary>
    public static Failure? Check(LoanTerms t, LoanSchedule computed, IReadOnlyList<BankRow> bank)
    {
        if (bank.Count == 0)
            return Invalid("rows", "A bank table has at least one row.");
        var byDate = computed.Rows.ToDictionary(r => r.DueDate);
        ScheduleRow? previous = null;
        var balance = 0m;
        for (var k = 0; k < bank.Count; k++)
        {
            var b = bank[k];
            foreach (var (column, amount) in new (string, decimal?)[]
                     { ("payment", b.Payment), ("interest", b.Interest), ("principal", b.Principal), ("insurance", b.Insurance), ("crd", b.Crd) })
                if (amount is { } x && (x < 0m || x >= AmountBound || decimal.Round(x, 2) != x))
                    return Invalid($"rows[{k}].{column}", "An amount is 0 or more, below 10^12, with at most two decimals.");
            if (!byDate.TryGetValue(b.DueDate, out var row))
                return Invalid($"rows[{k}].dueDate", "This date is not one of the loan's due dates.");
            if (previous is not null && row.Number <= previous.Number)
                return Invalid($"rows[{k}].dueDate", "The dates are distinct and increasing.");
            if (previous is not null && row.Number != previous.Number + 1)
                return Invalid($"rows[{k}].dueDate", "A due date is missing before this one.");
            var capitalising = t.Phases.FirstOrDefault(p => p.Seq == row.PhaseSeq)?.Mode == PhaseMode.DeferredTotal;
            if (b.Payment != b.Interest + b.Principal && !(capitalising && b.Payment == 0m && b.Principal == 0m))
                return Invalid($"rows[{k}].payment", "The payment is the interest plus the principal, without insurance.");
            if (previous is not null && b.Crd > balance && !capitalising)
                return Invalid($"rows[{k}].crd", "The remaining balance does not rise, except under a total deferral.");
            var after = b.Crd - (Early(t, row.Number) ?? 0m);
            if (after < 0m)
                return LoanValidation.Refuse($"events[{EventsAfter(t, row.Number).First().Index}].amount",
                    "An early repayment exceeds the bank's remaining balance.", LoanCodes.EventAboveBalance);
            (previous, balance) = (row, after);
        }
        return null;
    }

    /// <summary>Spec 3F2 §2: each bank row against the computed row of its date (its balance after the payment, before an early repayment), and the summary.</summary>
    public static ScheduleDiff Diff(LoanSchedule computed, IReadOnlyList<BankRow> bank)
    {
        var byDate = computed.Rows.ToDictionary(r => r.DueDate);
        var gaps = bank.Where(b => byDate.ContainsKey(b.DueDate)).Select(b =>
        {
            var c = byDate[b.DueDate];
            return new ScheduleGap(b.DueDate, b.Payment - c.Payment, b.Interest - c.Interest, b.Principal - c.Principal,
                b.Crd - (c.Balance + (c.EarlyRepayment ?? 0m)));
        }).ToList();
        var largest = gaps.MaxBy(Largest); // the first row at the largest gap
        var max = largest is null ? 0m : Largest(largest);
        return new ScheduleDiff(gaps, new ScheduleDiffSummary(max, max == 0m ? null : largest!.DueDate,
            gaps.FirstOrDefault(g => Math.Abs(g.Balance) > Divergence)?.DueDate, gaps.Sum(g => g.Interest),
            gaps.Count(g => Largest(g) > Rounding)));
    }

    private static decimal Largest(ScheduleGap g)
        => Math.Max(Math.Max(Math.Abs(g.Payment), Math.Abs(g.Interest)), Math.Max(Math.Abs(g.Principal), Math.Abs(g.Balance)));

    /// <summary>The early repayments the engine applies right after payment <paramref name="number"/>: dated from its due date to the next.</summary>
    private static IEnumerable<(EarlyRepayment Event, int Index)> EventsAfter(LoanTerms t, int number)
    {
        var (from, to) = (LoanScheduleBuilder.Due(t, number), LoanScheduleBuilder.Due(t, number + 1));
        return t.Events.Select((e, i) => (e, i)).Where(x => x.e.OnDate >= from && x.e.OnDate < to);
    }

    private static decimal? Early(LoanTerms t, int number)
        => EventsAfter(t, number).ToList() is { Count: > 0 } events ? events.Sum(x => x.Event.Amount) : null;

    private static Failure Invalid(string field, string message) => LoanValidation.Refuse(field, message, LoanCodes.BankScheduleInvalid);
}
