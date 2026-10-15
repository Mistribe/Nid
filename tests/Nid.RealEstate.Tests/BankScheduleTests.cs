using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

/// <summary>Spec 3F2 §2 and §5: a bank table's rules, the schedule it makes as the reference, its comparison (loans.md §6, §12).</summary>
public class BankScheduleTests
{
    private static LoanSchedule Build(LoanTerms t) => Loans.Ok(LoanScheduleBuilder.Build(t));

    private static LocalDate Due(int k) => Loans.First.PlusMonths(k - 1);

    /// <summary>A computed row as a bank writes it: the balance after its payment, before an early repayment made after it.</summary>
    private static BankRow AsBank(ScheduleRow r) => new(r.DueDate, r.Payment, r.Interest, r.Principal, null, r.Balance + (r.EarlyRepayment ?? 0m));

    /// <summary><paramref name="count"/> rows of <paramref name="s"/> from payment <paramref name="from"/>, as its bank writes them.</summary>
    private static List<BankRow> Table(LoanSchedule s, int from, int count) => s.Rows.Skip(from - 1).Take(count).Select(AsBank).ToList();

    private static List<(int, LocalDate, decimal, decimal, decimal, decimal, decimal?)> Figures(IEnumerable<ScheduleRow> rows)
        => rows.Select(r => (r.Number, r.DueDate, r.Payment, r.Interest, r.Principal, r.Balance, r.EarlyRepayment)).ToList();

    /// <summary>loans.md §4's golden case with 10 000 repaid on its 5th payment's date, the term reduced.</summary>
    private static LoanTerms Repaid() => Loans.Golden() with { Events = [new EarlyRepayment(Due(5), 10_000m, RepaymentEffect.ReduceTerm)] };

    /// <summary>10 000 at 2 %: a year of total deferral that capitalises, then an annuity over the 108 payments left.</summary>
    private static LoanTerms Deferred() => Loans.Annuity(10_000m, 0.02m, 120) with
    {
        Phases = [new LoanPhase(1, PhaseMode.DeferredTotal, 0.02m, RateType.Fixed, 12), new LoanPhase(2, PhaseMode.Annuity, 0.02m, RateType.Fixed)],
    };

    /// <summary>The theory's loans: each a state the loop carries past a table's last row (review of Task 1).</summary>
    private static LoanTerms Case(string loan) => loan switch
    {
        "repaid" => Repaid(),
        // The payment recalculated after a reduce-payment repayment on the table's last row.
        "reduce-payment" => Loans.Golden() with { Events = [new EarlyRepayment(Due(3), 10_000m, RepaymentEffect.ReducePayment)] },
        // The end a reduce-term repayment brings forward, kept into a phase that opens after the table.
        "two-rates" => Loans.Golden() with
        {
            Phases = [new LoanPhase(1, PhaseMode.Annuity, 0.035m, RateType.Fixed, 12), new LoanPhase(2, PhaseMode.Annuity, 0.036m, RateType.Fixed)],
            Events = [new EarlyRepayment(Due(5), 10_000m, RepaymentEffect.ReduceTerm)],
        },
        // A reduce-term repayment during a deferral, kept out of the annuity's payment.
        "deferred" => Deferred() with { Events = [new EarlyRepayment(Due(3), 1_000m, RepaymentEffect.ReduceTerm)] },
        // An imposed payment below the annuity: the loan runs past its 240 payments, the warning on the 241st.
        "extended" => Loans.Golden() with { Phases = [new LoanPhase(1, PhaseMode.Annuity, 0.035m, RateType.Fixed, PaymentOverride: 1_100m)] },
        // A prorated first payment: the bank's first interest beyond a month's is not part of the next payment (LoanScheduleBuilder).
        "prorata" => Loans.Golden() with { FirstPeriod = FirstPeriod.ProrataActual365 },
        _ => throw new ArgumentOutOfRangeException(nameof(loan)),
    };

    [Theory]
    [InlineData("repaid", 1, 3)]   // the first three payments: the calculation resumes after them, the repayment on the 5th included
    [InlineData("repaid", 4, 6)]   // a table that starts later: the calculation's rows come first; the repayment falls inside the table
    [InlineData("repaid", 1, 60)]
    [InlineData("repaid", 1, 0)]   // 0: the whole table, nothing left to calculate
    [InlineData("reduce-payment", 1, 3)]
    [InlineData("two-rates", 1, 6)]
    [InlineData("deferred", 1, 4)]
    [InlineData("prorata", 1, 1)]
    [InlineData("prorata", 1, 6)]
    [InlineData("extended", 1, 0)]
    [InlineData("extended", 1, 200)]
    public void A_table_equal_to_the_calculation_gives_the_calculation_back_with_its_rows_marked(string loan, int from, int count)
    {
        var terms = Case(loan);
        var computed = Build(terms);
        var rows = count == 0 ? computed.Rows.Count : count;

        var merged = Loans.Ok(BankSchedule.Merge(terms, computed, Table(computed, from, rows)));

        Assert.Equal(Figures(computed.Rows), Figures(merged.Rows));
        Assert.Equal(computed.Rows.Select(r => r.Number >= from && r.Number < from + rows), merged.Rows.Select(r => r.FromBank));
        Assert.Equal(computed.Warnings, merged.Warnings);
    }

    [Fact]
    public void A_total_deferral_capitalises_in_the_table_and_after_it()
    {
        var terms = Deferred();
        var computed = Build(terms);
        var table = Table(computed, 1, 2);
        Assert.Equal((0m, 16.67m, 10_016.67m, 10_033.36m), (table[0].Payment, table[0].Interest, table[0].Crd, table[1].Crd)); // it rises

        var merged = Loans.Ok(BankSchedule.Merge(terms, computed, table));

        Assert.Equal(Figures(computed.Rows), Figures(merged.Rows));
        Assert.Equal((0m, 16.72m, 10_050.08m), (merged.Rows[2].Payment, merged.Rows[2].Interest, merged.Rows[2].Balance));
    }

    /// <summary>Review Focus 3: a bank owing 500 less after its third payment; 10 000 repaid on the 5th, after the table.</summary>
    [Fact]
    public void An_early_repayment_after_the_tables_last_row_applies_to_the_banks_balance()
    {
        var terms = Repaid();
        var computed = Build(terms);
        var table = Table(computed, 1, 3);
        table[2] = table[2] with { Crd = table[2].Crd - 500m };

        var merged = Loans.Ok(BankSchedule.Merge(terms, computed, table)).Rows;

        Assert.Equal(197_765.18m, merged[2].Balance);
        Assert.Equal((1_159.92m, 576.82m, 583.10m, 197_182.08m, (decimal?)null, false),
            (merged[3].Payment, merged[3].Interest, merged[3].Principal, merged[3].Balance, merged[3].EarlyRepayment, merged[3].FromBank));
        Assert.Equal((575.11m, 584.81m, 186_597.27m, (decimal?)10_000m),
            (merged[4].Interest, merged[4].Principal, merged[4].Balance, merged[4].EarlyRepayment));
        Assert.Equal((223, 552.68m, 0m), (merged.Count, merged[^1].Payment, merged[^1].Balance));
    }

    [Fact]
    public void Each_rule_of_a_table_answers_at_its_row_and_column()
    {
        var terms = Loans.Golden();
        var computed = Build(terms);
        var table = Table(computed, 1, 3);
        (string Code, string? Field) Refused(params BankRow[] rows)
        {
            var failure = Loans.Fail(BankSchedule.Merge(terms, computed, rows));
            return (failure.Code, Loans.FieldOf(failure));
        }

        Assert.Equal((LoanCodes.BankScheduleInvalid, "rows"), Refused());
        Assert.Equal((LoanCodes.BankScheduleInvalid, "rows[1].dueDate"), Refused(table[0], table[1] with { DueDate = Due(2).PlusDays(1) })); // not a due date
        Assert.Equal((LoanCodes.BankScheduleInvalid, "rows[1].dueDate"), Refused(table[1], table[0]));                                    // decreasing
        Assert.Equal((LoanCodes.BankScheduleInvalid, "rows[1].dueDate"), Refused(table[0], table[2]));                                    // a missing one
        Assert.Equal((LoanCodes.BankScheduleInvalid, "rows[0].interest"), Refused(table[0] with { Interest = -1m }));
        Assert.Equal((LoanCodes.BankScheduleInvalid, "rows[0].crd"), Refused(table[0] with { Crd = 199_423.415m }));
        Assert.Equal((LoanCodes.BankScheduleInvalid, "rows[0].insurance"), Refused(table[0] with { Insurance = 1_000_000_000_000m }));
        Assert.Equal((LoanCodes.BankScheduleInvalid, "rows[2].crd"), Refused(table[0], table[1], table[2] with { Crd = table[1].Crd + 1m }));
    }

    /// <summary>Review Focus 5: the dates fit, but each payment holds the 30 € insurance and the table has no insurance column.</summary>
    [Fact]
    public void A_payment_that_includes_the_insurance_without_its_column_is_refused()
    {
        var terms = Loans.Golden() with { Insurances = [new LoanInsurance(InsuranceMode.FixedMonthly, 30m, null, Loans.First)] };
        var computed = Build(terms);
        var insured = Table(computed, 1, 2).Select(b => b with { Payment = b.Payment + 30m }).ToList();

        var failure = Loans.Fail(BankSchedule.Merge(terms, computed, insured));

        Assert.Equal((LoanCodes.BankScheduleInvalid, "rows[0].payment"), (failure.Code, Loans.FieldOf(failure)));
        // With its column, the same rows are the bank's, the insurance its own.
        var detailed = Table(computed, 1, 2).Select(b => b with { Insurance = 30m }).ToList();
        Assert.Equal(30m, Loans.Ok(BankSchedule.Merge(terms, computed, detailed)).Rows[0].Insurance);
    }

    [Fact]
    public void An_early_repayment_above_the_banks_balance_is_refused_at_its_amount()
    {
        var terms = Loans.Golden() with { Events = [new EarlyRepayment(Due(3), 10_000m, RepaymentEffect.ReduceTerm)] };
        var computed = Build(terms);
        var table = Table(computed, 1, 3);
        table[2] = table[2] with { Crd = 5_000m };

        var failure = Loans.Fail(BankSchedule.Merge(terms, computed, table));

        Assert.Equal((LoanCodes.EventAboveBalance, "events[0].amount"), (failure.Code, Loans.FieldOf(failure)));
    }

    [Fact]
    public void A_table_within_rounding_agrees()
    {
        var computed = Build(Loans.Golden());
        var table = Table(computed, 1, 12)
            .Select((b, i) => i < 5 ? b : i == 5
                ? b with { Interest = b.Interest + 0.01m, Principal = b.Principal - 0.01m, Crd = b.Crd + 0.01m }
                : b with { Crd = b.Crd + 0.01m })
            .ToList();

        var diff = BankSchedule.Diff(computed, table);

        Assert.Equal(12, diff.Rows.Count);
        Assert.Equal(new ScheduleDiffSummary(0.01m, Due(6), null, 0.01m, 0), diff.Summary);
    }

    /// <summary>loans.md §12: a bank charging 3.6 % from its 13th payment where the contract says 3.5 % throughout.</summary>
    [Fact]
    public void A_rate_disagreement_is_found_at_the_right_date()
    {
        var terms = Loans.Golden();
        var computed = Build(terms);
        var bank = Table(Build(terms with
        {
            Phases = [new LoanPhase(1, PhaseMode.Annuity, 0.035m, RateType.Fixed, 12), new LoanPhase(2, PhaseMode.Annuity, 0.036m, RateType.Fixed)],
        }), 1, 240);

        var diff = BankSchedule.Diff(computed, bank);

        Assert.All(diff.Rows.Take(12), g => Assert.Equal((0m, 0m, 0m, 0m), (g.Payment, g.Interest, g.Principal, g.Balance)));
        Assert.Equal((9.85m, 16.08m, -6.23m, 6.23m), (diff.Rows[12].Payment, diff.Rows[12].Interest, diff.Rows[12].Principal, diff.Rows[12].Balance));
        Assert.Equal(new ScheduleDiffSummary(450.14m, Due(139), Due(13), 2_246.41m, 228), diff.Summary);
        // The bank's own table is a valid reference for the same loan: its rows are read as they are.
        Assert.Equal(bank.Select(b => b.Crd), Loans.Ok(BankSchedule.Merge(terms, computed, bank)).Rows.Select(r => r.Balance));
    }
}
