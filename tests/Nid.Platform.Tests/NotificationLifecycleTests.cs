using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Notifications;
using NodaTime;

namespace Nid.Platform.Tests;

public class NotificationLifecycleTests
{
    private static readonly NotificationKind TenancyEnd = new("tenancy_end", "properties", NotificationLevel.Action, "properties", 90);
    private static readonly NotificationKind RentArrears = new("rent_arrears", "properties", NotificationLevel.Critical, "properties", null);
    private static readonly NotificationKind ShortLead = new("lease_check", "properties", NotificationLevel.Info, "properties", 7);

    private static readonly IReadOnlyDictionary<string, NotificationKind> Kinds = new[] { TenancyEnd, RentArrears, ShortLead }
        .ToDictionary(k => k.Kind);

    private static readonly ItemId Item = ItemId.New();

    private static LocalDate D(int y, int m, int d) => new(y, m, d);

    private static Signal Dated(NotificationKind kind, LocalDate due)
        => new(SignalKeys.Dated(kind.Kind, Item, due), kind.Kind, [Item], due, new Dictionary<string, string>());

    private static Signal Undated(NotificationKind kind)
        => new(SignalKeys.Undated(kind.Kind, Item), kind.Kind, [Item], null, new Dictionary<string, string>());

    private static StoredNotification Row(string key, string state, LocalDate notifyOn, LocalDate? snoozedUntil = null, bool settled = false)
        => new(Guid.NewGuid(), key, state, notifyOn, snoozedUntil, settled);

    [Fact]
    public void New_dated_signal_is_pending_until_its_lead()
    {
        var signal = Dated(TenancyEnd, D(2026, 3, 31));

        var pending = Assert.IsType<LifecycleChange.Insert>(Assert.Single(NotificationLifecycle.Plan([], [signal], Kinds, D(2025, 12, 1))));
        Assert.Equal(D(2025, 12, 31), pending.NotifyOn);
        Assert.False(pending.Open);

        var open = Assert.IsType<LifecycleChange.Insert>(Assert.Single(NotificationLifecycle.Plan([], [signal], Kinds, D(2026, 1, 5))));
        Assert.True(open.Open);
    }

    [Fact]
    public void Undated_signal_opens_today()
    {
        var today = D(2026, 1, 5);
        var insert = Assert.IsType<LifecycleChange.Insert>(
            Assert.Single(NotificationLifecycle.Plan([], [Undated(RentArrears)], Kinds, today)));
        Assert.Equal(today, insert.NotifyOn);
        Assert.True(insert.Open);
    }

    [Fact]
    public void Dated_signal_of_a_kind_without_lead_notifies_today()
    {
        var today = D(2026, 1, 5);
        var insert = Assert.IsType<LifecycleChange.Insert>(
            Assert.Single(NotificationLifecycle.Plan([], [Dated(RentArrears, D(2026, 6, 1))], Kinds, today)));
        Assert.Equal(today, insert.NotifyOn);
        Assert.True(insert.Open);
    }

    [Fact]
    public void NotifyOn_is_never_after_the_due_date_and_may_be_past()
    {
        var past = NotificationLifecycle.NotifyOn(Dated(TenancyEnd, D(2026, 1, 10)), TenancyEnd, D(2026, 1, 5));
        Assert.Equal(D(2025, 10, 12), past);

        var negativeLead = TenancyEnd with { LeadDays = -3 };
        Assert.Equal(D(2026, 1, 10), NotificationLifecycle.NotifyOn(Dated(negativeLead, D(2026, 1, 10)), negativeLead, D(2026, 1, 5)));
    }

    [Fact]
    public void Known_open_key_changes_nothing()
    {
        var signal = Undated(RentArrears);
        var today = D(2026, 1, 5);
        Assert.Empty(NotificationLifecycle.Plan([Row(signal.Key, NotificationStates.Open, today)], [signal], Kinds, today));
    }

    [Fact]
    public void Pending_row_is_retimed_and_opened()
    {
        var signal = Dated(ShortLead, D(2026, 1, 15));
        var row = Row(signal.Key, NotificationStates.Pending, D(2026, 2, 1));

        var retime = Assert.IsType<LifecycleChange.Retime>(
            Assert.Single(NotificationLifecycle.Plan([row], [signal], Kinds, D(2026, 1, 10))));
        Assert.Equal(row.Id, retime.Id);
        Assert.Equal(D(2026, 1, 8), retime.NotifyOn);
        Assert.True(retime.Open);
    }

    [Fact]
    public void Pending_row_with_an_unchanged_date_opens_when_it_comes()
    {
        var signal = Dated(ShortLead, D(2026, 1, 15));
        var row = Row(signal.Key, NotificationStates.Pending, D(2026, 1, 8));

        Assert.Empty(NotificationLifecycle.Plan([row], [signal], Kinds, D(2026, 1, 7)));
        var open = Assert.IsType<LifecycleChange.OpenNow>(Assert.Single(NotificationLifecycle.Plan([row], [signal], Kinds, D(2026, 1, 8))));
        Assert.Equal(row.Id, open.Id);
    }

    [Fact]
    public void Absent_key_resolves_and_a_dismissed_one_is_only_settled()
    {
        var today = D(2026, 1, 5);
        var open = Row("a", NotificationStates.Open, today);
        var pending = Row("b", NotificationStates.Pending, today);
        var snoozed = Row("c", NotificationStates.Snoozed, today, today.PlusDays(3));
        var dismissed = Row("d", NotificationStates.Dismissed, today);
        var resolved = Row("e", NotificationStates.Resolved, today);

        var changes = NotificationLifecycle.Plan([open, pending, snoozed, dismissed, resolved], [], Kinds, today);

        Assert.Equal(
            new[] { open.Id, pending.Id, snoozed.Id },
            changes.OfType<LifecycleChange.Resolve>().Select(c => c.Id));
        Assert.Equal(dismissed.Id, Assert.Single(changes.OfType<LifecycleChange.SettleDismissed>()).Id);
        Assert.Equal(4, changes.Count);
    }

    [Fact]
    public void Snoozed_row_wakes_on_its_date()
    {
        var signal = Undated(RentArrears);
        var today = D(2026, 1, 5);

        var due = Row(signal.Key, NotificationStates.Snoozed, today, today);
        var wake = Assert.IsType<LifecycleChange.Wake>(Assert.Single(NotificationLifecycle.Plan([due], [signal], Kinds, today)));
        Assert.Equal(due.Id, wake.Id);

        var later = Row(signal.Key, NotificationStates.Snoozed, today, today.PlusDays(1));
        Assert.Empty(NotificationLifecycle.Plan([later], [signal], Kinds, today));
    }

    [Fact]
    public void Resolved_key_signalled_again_reopens()
    {
        var today = D(2026, 1, 5);

        var undated = Undated(RentArrears);
        var resolved = Row(undated.Key, NotificationStates.Resolved, today, settled: true);
        var open = Assert.IsType<LifecycleChange.Reopen>(Assert.Single(NotificationLifecycle.Plan([resolved], [undated], Kinds, today)));
        Assert.Equal(resolved.Id, open.Id);
        Assert.Equal(today, open.NotifyOn);
        Assert.True(open.Open);

        var later = Dated(ShortLead, D(2026, 2, 15));
        var pendingRow = Row(later.Key, NotificationStates.Resolved, today, settled: true);
        var pending = Assert.IsType<LifecycleChange.Reopen>(Assert.Single(NotificationLifecycle.Plan([pendingRow], [later], Kinds, today)));
        Assert.Equal(D(2026, 2, 8), pending.NotifyOn);
        Assert.False(pending.Open);
    }

    [Fact]
    public void Dismissed_key_absent_is_settled_once()
    {
        var today = D(2026, 1, 5);
        var dismissed = Row("d", NotificationStates.Dismissed, today);
        var settled = Row("e", NotificationStates.Dismissed, today, settled: true);

        var change = Assert.IsType<LifecycleChange.SettleDismissed>(Assert.Single(NotificationLifecycle.Plan([dismissed, settled], [], Kinds, today)));
        Assert.Equal(dismissed.Id, change.Id);
    }

    [Fact]
    public void Settled_dismissed_key_signalled_again_is_unsettled()
    {
        var signal = Undated(RentArrears);
        var today = D(2026, 1, 5);
        var settled = Row(signal.Key, NotificationStates.Dismissed, today, settled: true);

        var change = Assert.IsType<LifecycleChange.Unsettle>(Assert.Single(NotificationLifecycle.Plan([settled], [signal], Kinds, today)));
        Assert.Equal(settled.Id, change.Id);
    }

    [Fact]
    public void Dismissed_key_signalled_again_stays_dismissed()
    {
        var signal = Undated(RentArrears);
        var today = D(2026, 1, 5);
        Assert.Empty(NotificationLifecycle.Plan([Row(signal.Key, NotificationStates.Dismissed, today)], [signal], Kinds, today));
    }

    [Fact]
    public void SignalKeys_format()
    {
        var id = ItemId.New();
        Assert.Equal($"tenancy_end:{id.Value:D}:2027-03-31", SignalKeys.Dated("tenancy_end", id, D(2027, 3, 31)));
        Assert.EndsWith(":2026-09", SignalKeys.Monthly("rent_arrears", id, D(2026, 9, 14)));
        Assert.Equal($"rent_arrears:{id.Value:D}", SignalKeys.Undated("rent_arrears", id));
    }
}
