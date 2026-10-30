using NodaTime;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using static Nid.Subscriptions.Tests.TestData;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Subscriptions.Tests;

public class ContractCancellationTests
{
    private static readonly LocalDate Today = new(2026, 10, 30);

    private static LocalDate D(int y, int m, int d) => new(y, m, d);

    private static SubscriptionInput Running(
        ContractTerms? terms,
        LocalDate? cancelledAt = null,
        LocalDate? phaseEnd = null)
    {
        var input = Input([Phase(D(2024, 1, 1), phaseEnd, 10m)], [], cancelledAt: cancelledAt);
        return input with { Subscription = input.Subscription with { Terms = terms } };
    }

    private static CancellationOutlook Outlook(ContractTerms? terms, LocalDate? today = null,
        LocalDate? cancelledAt = null, LocalDate? phaseEnd = null)
    {
        var now = today ?? Today;
        var input = Running(terms, cancelledAt, phaseEnd);
        var timeline = SubscriptionTimeline.Build(input, now, now.PlusYears(2)).Ok();
        return ContractCancellation.Outlook(input, timeline, now);
    }

    private static void AssertDeadline(CancellationOutlook outlook, LocalDate last, LocalDate effective)
    {
        Assert.Equal(CancellationStatus.Deadline, outlook.Status);
        Assert.Equal(last, outlook.LastDayToCancel);
        Assert.Equal(effective, outlook.EffectiveOn);
    }

    private static readonly ContractTerms Home = new(D(2027, 3, 1), Months(2), null, D(2027, 3, 1));

    [Fact]
    public void Home_first_year()
    {
        AssertDeadline(Outlook(Home), D(2027, 1, 1), D(2027, 3, 1));

        var after = Outlook(Home, D(2027, 1, 2));
        Assert.Equal(CancellationStatus.FreeFrom, after.Status);
        Assert.Equal(D(2027, 3, 1), after.FreeOn);

        Assert.Equal(CancellationStatus.FreeNow, Outlook(Home, D(2027, 3, 1)).Status);
    }

    [Fact]
    public void Box_commitment_only()
    {
        var terms = new ContractTerms(null, null, D(2027, 3, 15), null);

        var outlook = Outlook(terms);
        Assert.Equal(CancellationStatus.FreeFrom, outlook.Status);
        Assert.Equal(D(2027, 3, 15), outlook.FreeOn);

        Assert.Equal(CancellationStatus.FreeNow, Outlook(terms, D(2027, 3, 15)).Status);
    }

    private static readonly ContractTerms Energy = new(D(2024, 11, 10), Months(1), null, null);

    [Fact]
    public void Tacit_renewal_rolls_a_year()
        => AssertDeadline(Outlook(Energy), D(2027, 10, 10), D(2027, 11, 10));

    [Fact]
    public void Last_day_is_included()
        => AssertDeadline(Outlook(Energy, D(2026, 10, 10)), D(2026, 10, 10), D(2026, 11, 10));

    [Fact]
    public void No_notice_means_the_anniversary_itself()
    {
        var terms = new ContractTerms(D(2027, 3, 1), null, null, null);

        AssertDeadline(Outlook(terms), D(2027, 3, 1), D(2027, 3, 1));
    }

    [Fact]
    public void Anniversary_not_before_itself()
    {
        var terms = new ContractTerms(D(2028, 1, 15), Months(1), null, null);

        AssertDeadline(Outlook(terms), D(2027, 12, 15), D(2028, 1, 15));
    }

    [Fact]
    public void Commitment_floors_anniversaries()
    {
        var terms = new ContractTerms(D(2024, 11, 10), null, D(2028, 1, 1), null);

        var outlook = Outlook(terms);
        Assert.Equal(CancellationStatus.Deadline, outlook.Status);
        Assert.Equal(D(2028, 11, 10), outlook.EffectiveOn);
    }

    [Fact]
    public void Commitment_outlasts_cancellable_from()
    {
        var terms = new ContractTerms(D(2024, 11, 10), null, D(2027, 6, 1), D(2026, 1, 1));

        var outlook = Outlook(terms);
        Assert.Equal(CancellationStatus.FreeFrom, outlook.Status);
        Assert.Equal(D(2027, 6, 1), outlook.FreeOn);
    }

    [Fact]
    public void Leap_anchor()
    {
        var terms = new ContractTerms(D(2024, 2, 29), null, null, null);

        Assert.Equal(D(2027, 2, 28), Outlook(terms, D(2027, 1, 1)).EffectiveOn);
        Assert.Equal(D(2028, 2, 29), Outlook(terms, D(2028, 1, 1)).EffectiveOn);
    }

    [Fact]
    public void Month_end_notice()
    {
        var terms = new ContractTerms(D(2024, 3, 31), Months(1), null, null);

        var outlook = Outlook(terms, D(2027, 1, 1));
        Assert.Equal(CancellationStatus.Deadline, outlook.Status);
        Assert.Equal(D(2027, 2, 28), outlook.LastDayToCancel);
    }

    [Fact]
    public void Cancelled_is_none()
        => Assert.Equal(CancellationStatus.None, Outlook(Energy, cancelledAt: D(2027, 1, 1)).Status);

    [Fact]
    public void Ended_is_none()
        => Assert.Equal(CancellationStatus.None, Outlook(Energy, phaseEnd: D(2025, 6, 1)).Status);

    [Fact]
    public void Not_renewing_is_none()
    {
        var input = Running(Energy);
        input = input with { Subscription = input.Subscription with { AutoRenew = false } };
        var timeline = SubscriptionTimeline.Build(input, Today, Today.PlusYears(2)).Ok();

        Assert.Equal(CancellationStatus.None, ContractCancellation.Outlook(input, timeline, Today).Status);
    }

    [Fact]
    public void Far_commitment_does_not_overflow()
    {
        var terms = new ContractTerms(D(2024, 11, 10), Months(1), D(9999, 12, 31), null);

        Assert.Equal(CancellationStatus.None, Outlook(terms).Status);
    }

    [Fact]
    public void Notice_too_long_is_refused()
    {
        var notice = Duration.Create(1001, DurationUnit.Year).Ok();

        Assert.Equal("subscriptions.notice_too_long", new ContractTerms(D(2027, 1, 1), notice, null, null).Validate()?.Code);
    }

    [Fact]
    public void No_terms_is_none()
    {
        Assert.Equal(CancellationStatus.None, Outlook(null).Status);
        Assert.Equal(CancellationStatus.None, Outlook(new ContractTerms(null, null, null, null)).Status);
    }

    [Fact]
    public void Validate_rejects_a_notice_without_a_date()
        => Assert.Equal("subscriptions.notice_without_date",
                        new ContractTerms(null, Months(1), null, null).Validate()?.Code);

    [Fact]
    public void Validate_rejects_a_non_positive_notice()
        => Assert.Equal("subscriptions.duration_count",
                        new ContractTerms(D(2027, 1, 1), default(Duration), null, null).Validate()?.Code);

    [Fact]
    public void Validate_accepts_a_notice_with_a_commitment_only()
        => Assert.Null(new ContractTerms(null, Months(1), D(2027, 1, 1), null).Validate());
}
