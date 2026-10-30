using Nid.Banking.Domain;
using NodaTime;

namespace Nid.Banking.Tests;

public class ChargeMatchTests
{
    private static LocalDate D(int month, int day) => new(2026, month, day);

    private static Line L(LocalDate on, decimal amount, string currency = "EUR") => new(Guid.NewGuid(), Guid.NewGuid(), on, amount, currency, "STEM");

    private static Due C(int index, LocalDate on, decimal amount, bool credit = false, string currency = "EUR", int tolerance = 5)
        => new(index, on, amount, credit, currency, tolerance);

    [Fact]
    public void Same_amount_within_tolerance_agrees()
    {
        var line = L(D(3, 7), -12.99m);
        var charge = C(0, D(3, 5), 12.99m);
        Assert.Equal([0], ChargeMatch.Near(line, [charge]));
        Assert.True(ChargeMatch.Agrees(line, charge, null));
    }

    [Fact]
    public void A_charge_outside_tolerance_is_not_near()
        => Assert.Empty(ChargeMatch.Near(L(D(3, 11), -12.99m), [C(0, D(3, 5), 12.99m)]));

    [Fact]
    public void A_credit_never_matches_a_debit_charge()
        => Assert.Empty(ChargeMatch.Near(L(D(3, 5), 12.99m), [C(0, D(3, 5), 12.99m)]));

    [Fact]
    public void Nearest_date_comes_first()
        => Assert.Equal([1, 0], ChargeMatch.Near(L(D(3, 5), -5m), [C(0, D(3, 7), 5m), C(1, D(3, 5), 5m)]));

    [Fact]
    public void A_foreign_charge_agrees_within_three_percent()
    {
        var charge = C(0, D(3, 5), 11m, currency: "USD");
        Assert.True(ChargeMatch.Agrees(L(D(3, 5), -10.49m), charge, 10.20m));
        Assert.False(ChargeMatch.Agrees(L(D(3, 5), -10.52m), charge, 10.20m));
    }

    [Fact]
    public void A_foreign_charge_without_rate_is_null()
        => Assert.Null(ChargeMatch.Agrees(L(D(3, 5), -10.49m), C(0, D(3, 5), 11m, currency: "USD"), null));

    [Fact]
    public void Two_equal_charges_are_no_candidate()
        => Assert.Null(ChargeMatch.SingleCandidate(L(D(3, 5), -9.99m), [C(0, D(3, 4), 9.99m), C(1, D(3, 6), 9.99m)], _ => null));

    [Fact]
    public void One_agreeing_charge_among_near_ones_is_the_candidate()
        => Assert.Equal(1, ChargeMatch.SingleCandidate(L(D(3, 5), -9.99m), [C(0, D(3, 4), 19.99m), C(1, D(3, 6), 9.99m)], _ => null));
}
