using Nid.RealEstate.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.RealEstate.Domain;

/// <summary>Issues #403 and #404: a renegotiation and an insurance quote, each read by the schedule engine; nothing is written.</summary>
public static class LoanSimulations
{
    private const int MaxPayments = 600;

    /// <summary>
    /// Issue #403: from the first payment after <paramref name="on"/>, the balance runs as one fixed-rate annuity at
    /// <paramref name="annualRate"/> over <paramref name="payments"/> payments (the payments left by default): the phases are
    /// cut at the last payment due on or before it and the loan's count becomes the payments made plus these. A phase already
    /// run whose payment the engine computed over the old count keeps it, imposed. When the payments made would still change
    /// (an earlier repayment's payment or end read over the count), the count stays and the new annuity is imposed instead;
    /// a loan running past its count then warns so, as any imposed payment does. <paramref name="fees"/> (bank, guarantee,
    /// penalty) are typed, never computed.
    /// </summary>
    public static Result<RenegotiationSimulation> Renegotiate(LoanTerms t, LocalDate on, decimal annualRate, int? payments, decimal fees)
    {
        if (annualRate < 0m || annualRate >= 1m) return Refuse<RenegotiationSimulation>("annualRate", "A rate is at least 0 and below 1.");
        if (fees < 0m) return Refuse<RenegotiationSimulation>("fees", "Fees are not negative.");
        var built = LoanScheduleBuilder.Build(t);
        if (!built.TryGetValue(out var before)) return Result.Failure<RenegotiationSimulation>(Failure(built));
        if (on < t.DisbursedOn) return Refuse<RenegotiationSimulation>("on", "A renegotiation is on or after the disbursement.");
        var k = before!.Rows.Count(r => r.DueDate <= on);
        if (k >= before.Rows.Count) return Refuse<RenegotiationSimulation>("on", "The loan has no payment left after this date.");
        var n = payments ?? before.Rows.Count - k;
        if (n < 1 || k + n > MaxPayments)
            return Refuse<RenegotiationSimulation>("payments", $"A renegotiated loan has 1 to {MaxPayments - k} payments left.");

        var phases = Cut(t.Phases, k);
        var renegotiated = new LoanPhase(phases.Count + 1, PhaseMode.Annuity, annualRate, RateType.Fixed);
        var opening = k > 0 ? before.Rows[k - 1].Balance : t.Principal - t.Events.Where(e => e.OnDate < t.FirstPaymentDate).Sum(e => e.Amount);
        var annuity = LoanScheduleBuilder.Annuity(opening, annualRate, n);
        var pinned = k + n == t.TotalPayments ? phases : Pin(t, phases, before.Rows.Take(k).ToList());
        var terms = t with { Phases = [.. pinned, renegotiated], TotalPayments = k + n };
        var after = LoanScheduleBuilder.Build(terms);
        if (k > 0 && (!after.TryGetValue(out var read) || !read!.Rows.Take(k).SequenceEqual(before.Rows.Take(k)) || read.Rows[k].Payment != annuity))
        {
            terms = t with { Phases = [.. phases, renegotiated with { PaymentOverride = annuity }] };
            after = LoanScheduleBuilder.Build(terms);
        }
        // A repayment recorded after the date may no longer fit a shorter loan: the duration is what to change.
        if (!after.TryGetValue(out var schedule)) return Refuse<RenegotiationSimulation>("payments", Failure(after).Message, Failure(after).Code);

        var current = Side(before, k, 0m);
        var next = Side(schedule!, k, fees);
        var saving = current.TotalCost - next.TotalCost;
        LocalDate? breakEven = null;
        if (saving > 0m)
        {
            var saved = 0m;
            for (var j = k; breakEven is null; j++)
            {
                saved += Cost(before.Rows, j) - Cost(schedule!.Rows, j);
                if (saved >= fees) breakEven = (j < schedule.Rows.Count ? schedule.Rows[j] : before.Rows[j]).DueDate;
            }
        }
        return Result.Success(new RenegotiationSimulation(on, annualRate, n, current, next, saving, breakEven, terms));
    }

    /// <summary>The phases of the first <paramref name="k"/> payments: the one running at payment k ends there, the later ones go.</summary>
    private static List<LoanPhase> Cut(IReadOnlyList<LoanPhase> phases, int k)
    {
        var kept = new List<LoanPhase>();
        var left = k;
        foreach (var phase in phases.OrderBy(p => p.Seq))
        {
            if (left == 0) break;
            var take = phase.Payments is { } count && count < left ? count : left;
            kept.Add(phase with { Payments = take });
            left -= take;
        }
        // Past the last phase with a count, the engine projects at the last rate: the last phase kept runs to payment k.
        if (left > 0) kept[^1] = kept[^1] with { Payments = kept[^1].Payments + left };
        return kept;
    }

    /// <summary>
    /// The annuity phases run whose payment the engine computed keep it, imposed: the payment of their first row (a prorated
    /// first row carries more interest than a month's, so a phase starting there reads its second).
    /// </summary>
    private static List<LoanPhase> Pin(LoanTerms t, List<LoanPhase> phases, IReadOnlyList<ScheduleRow> made)
        => phases.Select(p => p.Mode != PhaseMode.Annuity || p.PaymentOverride is not null
                ? p
                : made.FirstOrDefault(r => r.PhaseSeq == p.Seq && !(r.Number == 1 && t.FirstPeriod == FirstPeriod.ProrataActual365)) is { } row
                    ? p with { PaymentOverride = row.Payment }
                    : p)
            .ToList();

    private static decimal Cost(IReadOnlyList<ScheduleRow> rows, int j) => j < rows.Count ? rows[j].Interest + rows[j].Insurance : 0m;

    private static RenegotiationSide Side(LoanSchedule s, int k, decimal fees)
    {
        var left = s.Rows.Skip(k).ToList();
        var interest = left.Sum(r => r.Interest);
        var insurance = left.Sum(r => r.Insurance);
        return new RenegotiationSide(left[0].Payment, s.Rows[^1].DueDate, left.Count, interest, insurance, fees, interest + insurance + fees);
    }

    /// <summary>
    /// Issue #404: each insurance line still due after <paramref name="on"/> (one insured person each), what it costs over the
    /// payments left as it is and, for a line <paramref name="quotes"/> names by its index, with the quote: the engine reads one
    /// line at a time, so a rate on the remaining capital follows the balance of each payment. As for a renegotiation, a payment
    /// due on <paramref name="on"/> is made: the quote takes the line over from the first payment after it (on its own start
    /// when later) until the line's end, and the line stops there, the engine charging a line from its start to before its end.
    /// </summary>
    public static Result<InsuranceComparison> CompareInsurance(LoanTerms t, LocalDate on, IReadOnlyList<(int Index, LoanInsurance Quote)> quotes)
    {
        var built = LoanScheduleBuilder.Build(t);
        if (!built.TryGetValue(out var schedule)) return Result.Failure<InsuranceComparison>(Failure(built));
        if (schedule!.Rows.FirstOrDefault(r => r.DueDate > on) is not { DueDate: var from })
            return Refuse<InsuranceComparison>("on", "The loan has no payment left after this date.");
        bool Running(LoanInsurance i) => i.EndsOn is not { } end || end > from;

        var byIndex = new Dictionary<int, LoanInsurance>();
        for (var j = 0; j < quotes.Count; j++)
        {
            var (index, quote) = quotes[j];
            if (index < 0 || index >= t.Insurances.Count || !Running(t.Insurances[index]) || byIndex.ContainsKey(index))
                return Refuse<InsuranceComparison>($"quotes[{j}].insurance", "A quote is for an insurance still due from this date, once.");
            var line = t.Insurances[index];
            var taken = quote with { StartsOn = line.StartsOn > from ? line.StartsOn : from, EndsOn = line.EndsOn };
            var read = Left(t, taken, from);
            if (read.TryGetFailure(out var failure))
                return Refuse<InsuranceComparison>(
                    failure!.Metadata is { } m && m.TryGetValue("field", out var f) && f is string field ? field.Replace("insurances[0]", $"quotes[{j}]") : $"quotes[{j}]",
                    failure.Message);
            byIndex[index] = taken;
        }

        var lines = new List<InsuranceComparisonLine>();
        var insurances = new List<ProposedInsurance>();
        for (var i = 0; i < t.Insurances.Count; i++)
        {
            var line = t.Insurances[i];
            if (!byIndex.TryGetValue(i, out var quote))
            {
                insurances.Add(new ProposedInsurance(i, line));
                if (Running(line)) lines.Add(new InsuranceComparisonLine(i, line, Value(Left(t, line, from)), null, null));
                continue;
            }
            // The line stops where the quote takes over; one starting later is replaced whole.
            if (line.StartsOn < from) insurances.Add(new ProposedInsurance(i, line with { EndsOn = from }));
            insurances.Add(new ProposedInsurance(null, quote));
            lines.Add(new InsuranceComparisonLine(i, line, Value(Left(t, line, from)), quote, Value(Left(t, quote, from))));
        }
        var currentLeft = lines.Sum(l => l.CurrentLeft);
        return Result.Success(new InsuranceComparison(on, lines, currentLeft, lines.Sum(l => l.QuoteLeft ?? l.CurrentLeft), insurances));
    }

    /// <summary>One line's premiums due from <paramref name="from"/>, the engine reading the loan with that line alone.</summary>
    private static Result<decimal> Left(LoanTerms t, LoanInsurance line, LocalDate from)
    {
        var built = LoanScheduleBuilder.Build(t with { Insurances = [line] });
        return built.TryGetValue(out var s) ? Result.Success(s!.Rows.Where(r => r.DueDate >= from).Sum(r => r.Insurance)) : Result.Failure<decimal>(Failure(built));
    }

    private static decimal Value(Result<decimal> r) => r.TryGetValue(out var v) ? v : throw new InvalidOperationException("Checked above.");

    private static Failure Failure<T>(Result<T> r) where T : notnull
        => r.TryGetFailure(out var f) ? f! : throw new InvalidOperationException("A failure was expected.");

    private static Result<T> Refuse<T>(string field, string message, string code = LoanCodes.InvalidLoan) where T : notnull
        => Result.Failure<T>(LoanValidation.Refuse(field, message, code));
}
