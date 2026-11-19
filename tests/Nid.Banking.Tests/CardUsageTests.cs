using Nid.Banking.Domain;
using NodaTime;

namespace Nid.Banking.Tests;

/// <summary>#362: the payments that count towards a card's minimum usage, and when the month's end is near.</summary>
public class CardUsageTests
{
    private static readonly LocalDate From = new(2025, 10, 1), To = new(2025, 10, 28);

    private static (LocalDate, decimal, string) L(int day, decimal amount, string label) => (new LocalDate(2025, 10, day), amount, label);

    [Fact]
    public void Debits_of_the_month_count_refunds_never_and_withdrawals_only_when_asked()
    {
        (LocalDate, decimal, string)[] lines = [
            L(2, -12m, "CB BOULANGERIE"), L(3, 5m, "AVOIR CB"), L(4, -20m, "RETRAIT DAB 03 10"), L(5, -40m, "ATM WITHDRAWAL LONDON"),
            (new LocalDate(2025, 9, 30), -9m, "CB SEPTEMBRE"), L(29, -3m, "CB DEMAIN"),
        ];

        Assert.Equal(1, CardUsage.Payments(lines, From, To, countWithdrawals: false));
        Assert.Equal(3, CardUsage.Payments(lines, From, To, countWithdrawals: true));
    }

    [Fact]
    public void A_label_that_only_contains_dab_inside_a_word_is_a_payment()
        => Assert.Equal(1, CardUsage.Payments([L(2, -12m, "CB DABOVILLE")], From, To, countWithdrawals: false));

    [Theory]
    [InlineData(27, 5, true)]
    [InlineData(26, 5, false)]
    [InlineData(31, 1, true)]
    [InlineData(30, 1, false)]
    public void The_alert_is_due_in_the_last_days_of_the_month(int day, int days, bool due)
        => Assert.Equal(due, CardUsage.Due(new LocalDate(2025, 10, day), days));
}
