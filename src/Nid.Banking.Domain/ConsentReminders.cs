using NodaTime;

namespace Nid.Banking.Domain;

/// <summary>Spec §5: a consent is reminded two weeks and three days before it ends, each reminder once (it is part of the signal's key).</summary>
public static class ConsentReminders
{
    /// <summary>The reminders due: 14 when 14 days or fewer are left, and 3 when 3 or fewer are.</summary>
    public static IReadOnlyList<int> Thresholds(LocalDate validUntil, LocalDate today)
    {
        var left = Period.Between(today, validUntil, PeriodUnits.Days).Days;
        return new[] { 14, 3 }.Where(t => left <= t).ToList();
    }
}
