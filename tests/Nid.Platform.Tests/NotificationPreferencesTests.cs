using Nid.Platform.Contracts;
using Nid.Platform.Notifications;
using NodaTime;

namespace Nid.Platform.Tests;

/// <summary>Notifications design §5.2: a person's own lead times apply to dated kinds only.</summary>
public class NotificationPreferencesTests
{
    private static readonly NotificationKind Dated = new("tenancy_end", "due_date", NotificationLevel.Action, "properties", 90);
    private static readonly NotificationKind Undated = new("rent_arrears", "finding", NotificationLevel.Action, "properties", null);

    [Fact]
    public void Apply_overrides_only_dated_kinds()
    {
        var prefs = NotificationPreferences.Defaults with
        {
            LeadDays = new Dictionary<string, int> { ["tenancy_end"] = 60, ["rent_arrears"] = 10 },
        };

        Assert.Equal(60, prefs.Apply(Dated).LeadDays);
        Assert.Equal(Undated, prefs.Apply(Undated));
    }

    [Fact]
    public void A_kind_without_an_entry_keeps_its_default()
    {
        Assert.Equal(Dated, NotificationPreferences.Defaults.Apply(Dated));
    }

    [Fact]
    public void Defaults_are_off_immediate_digest_weekly_monday()
    {
        var d = NotificationPreferences.Defaults;

        Assert.Equal((false, ActionMode.Immediate, InfoMode.Digest, DigestFrequency.Weekly, IsoDayOfWeek.Monday, 0),
            (d.EmailEnabled, d.Action, d.Info, d.Frequency, d.Weekday, d.Version));
        Assert.Empty(d.LeadDays);
    }
}
