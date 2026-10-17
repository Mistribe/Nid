using Nid.Platform.Contracts;
using Nid.RealEstate.Application;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Tests;

/// <summary>Spec 4D2 §3: the one check of a table's rows, shared by the pasted text and a proposal; its paths are the rows' own.</summary>
public class BankCheckTests
{
    private static readonly LoanTerms Terms = Loans.Golden();
    private static readonly LoanSchedule Computed = Loans.Ok(LoanScheduleBuilder.Build(Terms));

    /// <summary>The first <paramref name="count"/> computed rows, as a bank writes them.</summary>
    private static List<BankRow> Table(int count)
        => Computed.Rows.Take(count).Select(r => new BankRow(r.DueDate, r.Payment, r.Interest, r.Principal, null, r.Balance)).ToList();

    private static string PathOf(Result<IReadOnlyList<BankRow>> result)
        => result.TryGetFailure(out var failure) ? FieldErrors.Of(failure!)![0].Path : "accepted";

    [Fact]
    public void Rows_that_fit_are_accepted_and_each_refusal_names_its_row_or_the_table()
    {
        var gap = Table(3);
        gap.RemoveAt(1);
        var paid = Table(3);
        paid[0] = paid[0] with { Payment = paid[0].Payment + 50m };

        Assert.Equal("accepted", PathOf(BankCheck.Rows(Table(3), Terms, Computed)));
        Assert.Equal("rows[1].dueDate", PathOf(BankCheck.Rows(gap, Terms, Computed)));
        Assert.Equal("rows[0].payment", PathOf(BankCheck.Rows(paid, Terms, Computed)));
        Assert.Equal("rows", PathOf(BankCheck.Rows([], Terms, Computed)));
        Assert.Equal("rows", PathOf(BankCheck.Rows(Enumerable.Repeat(Table(1)[0], LoanScheduleBuilder.MaxRows + 1).ToList(), Terms, Computed)));
    }

    [Fact]
    public void A_pasted_table_over_the_row_limit_is_refused_at_the_text()
    {
        var line = "15/11/2020;1159,92;583,33;576,59;199423,41\n";

        var read = BankCheck.Read(string.Concat(Enumerable.Repeat(line, LoanScheduleBuilder.MaxRows + 1)), Terms, Computed);

        Assert.Equal("text", PathOf(read));
    }

    [Fact]
    public void An_early_repayment_the_table_cannot_take_is_refused_at_the_table()
    {
        // 199 000 repaid right after the first payment: the bank's balance after it, 198 000, cannot take it.
        var terms = Loans.Golden() with { Events = [new EarlyRepayment(Loans.First, 199_000m, RepaymentEffect.ReduceTerm)] };
        var computed = Loans.Ok(LoanScheduleBuilder.Build(terms));

        var refused = BankCheck.Rows([new BankRow(Loans.First, 1159.92m, 583.33m, 576.59m, null, 198_000m)], terms, computed);

        Assert.True(refused.TryGetFailure(out var failure));
        Assert.Equal((LoanCodes.EventAboveBalance, "rows"), (failure!.Code, FieldErrors.Of(failure)![0].Path));
    }
}
