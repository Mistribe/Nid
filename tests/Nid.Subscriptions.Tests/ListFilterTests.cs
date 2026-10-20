using Nid.Subscriptions.Application;
using Nid.Subscriptions.Application.Catalog;
using NodaTime;

namespace Nid.Subscriptions.Tests;

public class ListFilterTests
{
    private static readonly LocalDate Today = new(2025, 10, 15);

    private static ProviderSummaryView Provider(params string[] labels)
        => new("p", "P", "catalog", labels.Select(l => new LabelView(l, l, "#000")).ToList(), null);

    private static ListFilter Only(string? q = null, ListStatus status = ListStatus.All, params string[] labels) => new(q, status, labels);

    [Fact]
    public void Q_matches_ignoring_accents_and_case()
        => Assert.True(Only("deezer").Matches("Déézer Famille", null, null, Today));

    [Fact]
    public void Ended_means_expires_before_today()
    {
        var yesterday = Today.PlusDays(-1);
        Assert.True(Only(status: ListStatus.Ended).Matches("x", null, yesterday, Today));
        Assert.False(Only(status: ListStatus.Active).Matches("x", null, yesterday, Today));
        Assert.True(Only(status: ListStatus.Active).Matches("x", null, Today, Today));
        Assert.False(Only(status: ListStatus.Ended).Matches("x", null, Today, Today));
    }

    [Fact]
    public void No_expiry_is_active()
    {
        Assert.True(Only(status: ListStatus.Active).Matches("x", null, null, Today));
        Assert.False(Only(status: ListStatus.Ended).Matches("x", null, null, Today));
    }

    [Fact]
    public void Labels_match_any_provider_label()
        => Assert.True(Only(labels: ["music", "vpn"]).Matches("x", Provider("streaming", "music"), null, Today));

    [Fact]
    public void Labels_never_match_a_subscription_without_provider()
        => Assert.False(Only(labels: ["music"]).Matches("x", null, null, Today));

    [Fact]
    public void None_matches_everything()
        => Assert.True(ListFilter.None.Matches("x", null, Today.PlusDays(-9), Today));
}
