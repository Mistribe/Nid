using Nid.Banking.Domain;
using NodaTime;

namespace Nid.Banking.Tests;

public class InternalTransfersTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    private static Line L(Guid account, int day, decimal amount) => new(Guid.NewGuid(), account, new LocalDate(2026, 3, day), amount, "EUR", "VIR");

    [Fact]
    public void A_debit_and_a_credit_three_days_apart_pair()
    {
        var debit = L(A, 1, -500m);
        var credit = L(B, 4, 500m);
        Assert.Equal([(debit.Id, credit.Id)], InternalTransfers.Pair([debit, credit]));
    }

    [Fact]
    public void Same_account_never_pairs()
        => Assert.Empty(InternalTransfers.Pair([L(A, 1, -500m), L(A, 1, 500m)]));

    [Fact]
    public void Accounts_not_pairable_leave_the_line_to_a_pairable_one()
    {
        var c = Guid.NewGuid();
        var debit = L(A, 1, -500m);
        var farther = L(c, 3, 500m);
        Assert.Equal([(debit.Id, farther.Id)], InternalTransfers.Pair([debit, L(B, 1, 500m), farther], (d, cr) => cr != B));
    }

    [Fact]
    public void Four_days_apart_do_not_pair()
        => Assert.Empty(InternalTransfers.Pair([L(A, 1, -500m), L(B, 5, 500m)]));

    [Fact]
    public void Each_line_pairs_once_closest_first()
    {
        var debit = L(A, 1, -500m);
        var far = L(B, 3, 500m);
        var near = L(B, 2, 500m);
        Assert.Equal([(debit.Id, near.Id)], InternalTransfers.Pair([debit, far, near]));
    }
}
