using Nid.Banking.Domain;
using NodaTime;

namespace Nid.Banking.Tests;

public class ConsentRemindersTests
{
    [Theory]
    [InlineData(20, new int[0])]
    [InlineData(14, new[] { 14 })]
    [InlineData(4, new[] { 14 })]
    [InlineData(3, new[] { 14, 3 })]
    [InlineData(0, new[] { 14, 3 })]
    public void A_reminder_is_due_once_its_lead_is_reached(int daysLeft, int[] expected)
    {
        var today = new LocalDate(2026, 3, 1);
        Assert.Equal(expected, ConsentReminders.Thresholds(today.PlusDays(daysLeft), today));
    }
}
