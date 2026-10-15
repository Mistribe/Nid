using Nid.RealEstate.Application;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;

namespace Nid.RealEstate.Tests;

/// <summary>Spec 3F2 §2: every reader goes through <see cref="LoanReading.Schedule"/>, every rebuilding write through <see cref="LoanReading.Build"/>.</summary>
public class LoanReadingBankTests
{
    private static readonly LoanSchedule Golden = Loans.Ok(LoanScheduleBuilder.Build(Loans.Golden()));

    /// <summary>The golden case's first 12 payments as a bank that insures 30 a month and owes 100 less throughout.</summary>
    private static List<BankRow> Table() => Golden.Rows.Take(12)
        .Select(r => new BankRow(r.DueDate, r.Payment, r.Interest, r.Principal, 30m, r.Balance - 100m)).ToList();

    private static StoredLoan WithTable(ScheduleSource source) => Loans.Stored() with
    {
        Source = source, Bank = Table().Select(b => new StoredBankRow(Guid.CreateVersion7(), b)).ToList(),
    };

    /// <summary>Review Focus 2: today within the table reads the bank; today after it reads the calculation from the bank's balance.</summary>
    [Fact]
    public void The_figures_read_the_banks_rows_up_to_today_and_the_calculation_after_them()
    {
        var loan = WithTable(ScheduleSource.Bank);

        var within = LoanViews.Detail(Loans.Item(), loan, Loans.First.PlusMonths(5).PlusDays(3), canWrite: true).Figures; // after the 6th payment
        Assert.Equal((LoanReading.Amount(Golden.Rows[5].Balance - 100m), "180.00"), (within.Balance, within.InsurancePaid));
        Assert.Equal((Loans.First.PlusMonths(6), "1189.92"), (within.NextPaymentOn!.Value, within.NextPaymentTotal));

        var merged = Loans.Ok(BankSchedule.Merge(Loans.Golden(), Golden, Table()));
        var after = LoanViews.Detail(Loans.Item(), loan, Loans.First.PlusMonths(19), canWrite: true).Figures; // the 20th payment, 8 after the table
        Assert.Equal((LoanReading.Amount(merged.Rows[19].Balance), "360.00", "1159.92"), (after.Balance, after.InsurancePaid, after.NextPaymentTotal));
        Assert.NotEqual(LoanReading.Amount(Golden.Rows[19].Balance), after.Balance);
    }

    [Fact]
    public void A_stored_table_is_read_only_once_the_bank_is_the_reference()
    {
        var on = Loans.First.PlusMonths(5).PlusDays(3);

        Assert.Equal(LoanReading.Amount(Golden.Rows[5].Balance), LoanViews.Summary(Loans.Item(), WithTable(ScheduleSource.Computed), on, true).RemainingBalance);
        Assert.Equal(LoanReading.Amount(Golden.Rows[5].Balance - 100m), LoanViews.Summary(Loans.Item(), WithTable(ScheduleSource.Bank), on, true).RemainingBalance);
    }

    [Fact]
    public void A_write_the_stored_table_no_longer_fits_is_refused_at_bankSchedule()
    {
        var moved = Loans.Golden() with { FirstPaymentDate = Loans.First.PlusDays(5) }; // the table's dates are no longer due dates

        var failure = Loans.Fail(LoanReading.Build(moved, Table()));

        Assert.Equal((LoanCodes.BankScheduleInvalid, "bankSchedule"), (failure.Code, Loans.FieldOf(failure)));
        Assert.True(LoanReading.Build(Loans.Golden(), Table()).IsSuccess);
        Assert.True(LoanReading.Build(moved, []).IsSuccess);
    }
}
