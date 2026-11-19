using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using NodaTime;

namespace Nid.Banking.Tests;

/// <summary>#365: Nid's count of an account's reads, an estimate of the bank's limit.</summary>
public class BankReadsTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 11, 19, 14, 0);
    private static readonly Guid Account = Guid.NewGuid();

    private static BankRead Read(int hoursAgo, bool attended = false, bool limited = false) => new(Account, Now - Duration.FromHours(hoursAgo), attended, limited);

    [Fact]
    public void Counts_the_unattended_reads_of_the_last_day_apart_from_the_attended_ones_and_the_refusals()
    {
        var count = BankReads.Count([Read(30), Read(20), Read(10), Read(2, attended: true), Read(1, limited: true), Read(50, limited: true)], Now);

        Assert.Equal((2, 1), (count.Unattended, count.Attended));
        // The oldest of the window (20 hours ago) stops counting in 4 hours.
        Assert.Equal(Now + Duration.FromHours(4), count.FreesAt);
        Assert.Equal((Now - Duration.FromHours(1), false), (count.LimitedAt, count.LimitedAttended));
    }

    [Fact]
    public void Nothing_read_is_nothing_counted()
    {
        Assert.Equal(ReadCount.None, BankReads.Count([], Now));
        Assert.Equal(ReadCount.None, BankReads.Worst([]));
    }

    [Fact]
    public void A_connection_shows_its_busiest_account_and_its_latest_refusal()
    {
        var busy = BankReads.Count([Read(5), Read(3)], Now);
        var refused = BankReads.Count([Read(4), Read(1, attended: true, limited: true)], Now);

        var worst = BankReads.Worst([refused, busy]);

        Assert.Equal((2, Now + Duration.FromHours(19), Now - Duration.FromHours(1), true),
            (worst.Unattended, worst.FreesAt, worst.LimitedAt, worst.LimitedAttended));
    }
}
