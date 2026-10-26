using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Notifications;
using NodaTime;

namespace Nid.Platform.Tests;

/// <summary>Notifications design §4: a notification names the item and the date, never an amount.</summary>
public class NotificationTemplatesTests
{
    private static readonly ItemId Item = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static readonly (string Kind, string Module)[] Kinds =
    [
        ("subscription_renewal", "subscriptions"), ("subscription_expiry", "subscriptions"),
        ("subscriptions.ends_without_renewal", "subscriptions"),
        ("tenancy_end", "properties"), ("property_cost_due", "properties"), ("rent_arrears", "properties"),
        ("revision_due", "properties"), ("revision_lost", "properties"), ("irl_missing", "properties"),
        ("valuation_stale", "properties"),
        ("loan_phase_end", "loans"), ("loan_end", "loans"),
    ];

    private static NotificationRecord Record(string kind, LocalDate? dueOn = null, string key = "k")
        => new(Guid.NewGuid(), new PersonId(Guid.NewGuid()), kind, key, NotificationLevel.Info, [Item], dueOn, new LocalDate(2027, 3, 1),
            new Dictionary<string, string>(), NotificationStates.Open, null, null, Instant.FromUtc(2027, 3, 1, 0, 0), null, null);

    private static NotificationKind KindOf(string kind, string module) => new(kind, "test", NotificationLevel.Info, module, null);

    [Fact]
    public void Every_kind_has_a_title_and_a_link()
    {
        foreach (var (kind, module) in Kinds)
        {
            var rendered = NotificationTemplates.Render(Record(kind), KindOf(kind, module), new Dictionary<ItemId, string> { [Item] = "Maison" });
            Assert.False(string.IsNullOrWhiteSpace(rendered.Title), kind);
            Assert.StartsWith("/", rendered.Link);
            Assert.Contains(Item.Value.ToString("D"), rendered.Link);
            Assert.DoesNotContain("Maison", rendered.Title);
        }
    }

    [Fact]
    public void Body_has_the_name_and_the_date_never_an_amount()
    {
        var rendered = NotificationTemplates.Render(Record("tenancy_end", new LocalDate(2027, 3, 31)), KindOf("tenancy_end", "properties"),
            new Dictionary<ItemId, string> { [Item] = "Maison" });

        Assert.Equal("Maison — le 31 mars 2027", rendered.Body);
        Assert.Equal("Un bail arrive à échéance", rendered.Title);
        Assert.Equal($"/properties/{Item.Value:D}?tab=rental", rendered.Link);
    }

    [Fact]
    public void Findings_name_their_month_or_date_and_a_missing_name_is_an_element()
    {
        var none = new Dictionary<ItemId, string>();
        var arrears = NotificationTemplates.Render(Record("rent_arrears", key: $"rent_arrears:{Item.Value:D}:2027-02"), KindOf("rent_arrears", "properties"),
            new Dictionary<ItemId, string> { [Item] = "Studio" });
        var stale = NotificationTemplates.Render(Record("valuation_stale", key: $"valuation_stale:{Item.Value:D}:2025-06-15"), KindOf("valuation_stale", "properties"), none);
        var loan = NotificationTemplates.Render(Record("loan_end", new LocalDate(2027, 12, 1)), KindOf("loan_end", "loans"), none);

        Assert.Equal("Studio — depuis février 2027", arrears.Body);
        Assert.Equal("Élément — estimée le 15 juin 2025", stale.Body);
        Assert.Equal($"/properties/{Item.Value:D}?tab=valuations", stale.Link);
        Assert.Equal("Élément — le 1er décembre 2027", loan.Body);
        Assert.Equal($"/loans/{Item.Value:D}", loan.Link);
    }
}
