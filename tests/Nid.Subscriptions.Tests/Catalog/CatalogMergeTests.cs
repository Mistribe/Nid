using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain.Catalog;
using NodaTime;

namespace Nid.Subscriptions.Tests.Catalog;

public class CatalogMergeTests
{
    private const string Commit = "c1";
    private static readonly Instant Archived = Instant.FromUtc(2026, 1, 1, 0, 0);
    private static readonly Label StreamingLabel = new("streaming", "Streaming", "#6400F6", LabelOrigin.Catalog);
    private static readonly Label OtherLabel = new(CatalogLimits.OtherLabel, "Other", CatalogLimits.NeutralColor, LabelOrigin.Local);

    private static Provider Stored(string key, ProviderOrigin origin, string name = "Name", string[]? labels = null, Instant? archivedAt = null, string commit = Commit)
        => new(key, origin, name, null, null, labels ?? ["streaming"],
            origin == ProviderOrigin.Catalog ? commit : null,
            origin == ProviderOrigin.Custom ? PersonId.New() : null,
            archivedAt, null);

    private static CatalogProvider Entry(string key, string name = "Name", string[]? labels = null)
        => new(key, name, null, null, labels ?? ["streaming"]);

    private static CatalogReading Reading(params CatalogProvider[] providers)
        => new([new CatalogLabel("streaming", "Streaming", "#6400F6")], providers, [], [], []);

    private static CatalogSnapshot Snapshot(params Provider[] providers) => new(providers, [StreamingLabel, OtherLabel]);

    [Fact]
    public void A_new_key_becomes_a_catalog_provider_of_the_commit()
    {
        var plan = CatalogMerge.Plan(Snapshot(), Reading(Entry("netflix", "Netflix")), "c2");

        var written = Assert.Single(plan.Providers);
        Assert.Equal(("netflix", ProviderOrigin.Catalog, "Netflix", "c2"), (written.Key, written.Origin, written.Name, written.CatalogCommit));
        Assert.Equal(["netflix"], plan.Report.ProvidersAdded);
        Assert.Empty(plan.Archive);
    }

    [Fact]
    public void A_catalog_provider_is_updated_and_an_archived_one_restored()
    {
        var plan = CatalogMerge.Plan(
            Snapshot(Stored("netflix", ProviderOrigin.Catalog, "Old name"), Stored("hulu", ProviderOrigin.Catalog, archivedAt: Archived)),
            Reading(Entry("netflix", "Netflix"), Entry("hulu")), "c2");

        Assert.Equal(["hulu", "netflix"], plan.Report.ProvidersUpdated);
        Assert.All(plan.Providers, p => Assert.Equal(("c2", (Instant?)null), (p.CatalogCommit, p.ArchivedAt)));
        Assert.Equal("Netflix", Assert.Single(plan.Providers, p => p.Key == "netflix").Name);
    }

    [Fact]
    public void An_adopted_provider_becomes_the_catalog_one_with_the_same_key()
    {
        var plan = CatalogMerge.Plan(Snapshot(Stored("spotify", ProviderOrigin.Adopted, "Spotify", [CatalogLimits.OtherLabel])),
            Reading(Entry("spotify", "Spotify")), Commit);

        var written = Assert.Single(plan.Providers);
        Assert.Equal((ProviderOrigin.Catalog, Commit), (written.Origin, written.CatalogCommit));
        Assert.Equal(["streaming"], written.LabelKeys);
        Assert.Equal(["spotify"], plan.Report.AdoptedReplaced);
        Assert.Empty(plan.Report.AdoptedRemaining);
    }

    /// <summary>Review Focus 2: <c>Spotify</c>, stored through the API before 1C2b, is not the catalog's <c>spotify</c>.</summary>
    [Fact]
    public void An_adopted_key_differing_by_case_stays_adopted()
    {
        var plan = CatalogMerge.Plan(Snapshot(Stored("Spotify", ProviderOrigin.Adopted)), Reading(Entry("spotify")), Commit);

        Assert.Equal(["spotify"], plan.Report.ProvidersAdded);
        Assert.Empty(plan.Report.AdoptedReplaced);
        Assert.Equal(["Spotify"], plan.Report.AdoptedRemaining);
        Assert.DoesNotContain(plan.Providers, p => p.Key == "Spotify");
    }

    [Fact]
    public void A_custom_provider_is_never_overwritten_and_is_reported_as_a_conflict()
    {
        var plan = CatalogMerge.Plan(Snapshot(Stored("tinder-k3f9qa", ProviderOrigin.Custom, "Mine")), Reading(Entry("tinder-k3f9qa", "Tinder")), Commit);

        Assert.Empty(plan.Providers);
        Assert.Equal(["tinder-k3f9qa"], plan.Report.Conflicts);
        Assert.Empty(plan.Report.ProvidersAdded);
    }

    [Fact]
    public void A_provider_leaving_the_catalog_is_archived_once()
    {
        var plan = CatalogMerge.Plan(
            Snapshot(Stored("hulu", ProviderOrigin.Catalog), Stored("gone", ProviderOrigin.Catalog, archivedAt: Archived), Stored("mine-abc123", ProviderOrigin.Custom)),
            Reading(), Commit);

        Assert.Equal(["hulu"], plan.Archive);
        Assert.Equal(["hulu"], plan.Report.ProvidersArchived);
        Assert.Empty(plan.Providers);
    }

    [Fact]
    public void The_same_commit_twice_changes_nothing()
    {
        var plan = CatalogMerge.Plan(Snapshot(Stored("netflix", ProviderOrigin.Catalog, "Netflix")), Reading(Entry("netflix", "Netflix")), Commit);

        Assert.Empty(plan.Providers);
        Assert.Empty(plan.Labels);
        Assert.Empty(plan.Archive);
        Assert.Equal(["netflix"], plan.Report.ProvidersUnchanged);
        Assert.Empty(plan.Report.ProvidersUpdated);
        Assert.Empty(plan.Report.LabelsAdded);
        Assert.Empty(plan.Report.LabelsUpdated);
    }

    [Fact]
    public void Another_commit_with_the_same_content_is_unchanged_but_moves_the_commit()
    {
        var plan = CatalogMerge.Plan(Snapshot(Stored("netflix", ProviderOrigin.Catalog, "Netflix", commit: "c0")), Reading(Entry("netflix", "Netflix")), Commit);

        Assert.Equal(["netflix"], plan.Report.ProvidersUnchanged);
        Assert.Equal(Commit, Assert.Single(plan.Providers).CatalogCommit);
    }

    [Fact]
    public void Labels_are_added_or_updated_and_a_local_one_is_left_alone()
    {
        var current = new CatalogSnapshot([], [StreamingLabel with { Name = "Old" }, OtherLabel]);
        var reading = new CatalogReading(
            [new CatalogLabel("streaming", "Streaming", "#6400F6"), new CatalogLabel("vod", "VOD", "#F06292"), new CatalogLabel(CatalogLimits.OtherLabel, "Autre", "#000000")],
            [], [], [], []);

        var plan = CatalogMerge.Plan(current, reading, Commit);

        Assert.Equal(["streaming"], plan.Report.LabelsUpdated);
        Assert.Equal(["vod"], plan.Report.LabelsAdded);
        Assert.Equal(["streaming", "vod"], plan.Labels.Select(l => l.Key).Order(StringComparer.Ordinal));
        Assert.All(plan.Labels, l => Assert.Equal(LabelOrigin.Catalog, l.Origin));
    }

    [Fact]
    public void What_the_reading_found_travels_into_the_report()
    {
        var reading = Reading() with
        {
            LabelsCreated = ["home"],
            Duplicates = [new CatalogDuplicate(CatalogReader.LabelsFile, "hosting")],
            Refused = [new CatalogRefusal(CatalogReader.ProvidersFile, 3, "Netflix", CatalogCodes.InvalidKey)],
        };

        var report = CatalogMerge.Plan(Snapshot(), reading, Commit).Report;

        Assert.Equal(["home"], report.LabelsCreated);
        Assert.Equal([new CatalogDuplicate(CatalogReader.LabelsFile, "hosting")], report.Duplicates);
        Assert.Equal([new CatalogRefusal(CatalogReader.ProvidersFile, 3, "Netflix", CatalogCodes.InvalidKey)], report.Refused);
    }

    [Fact]
    public void A_changed_icon_url_updates_the_provider()
    {
        var stored = Stored("netflix", ProviderOrigin.Catalog) with { IconUrl = "https://1000logos.net/old.png" };

        var plan = CatalogMerge.Plan(Snapshot(stored), Reading(Entry("netflix") with { IconUrl = "https://1000logos.net/new.png" }), Commit);

        Assert.Equal(["netflix"], plan.Report.ProvidersUpdated);
        Assert.Equal("https://1000logos.net/new.png", Assert.Single(plan.Providers).IconUrl);
    }

    [Fact]
    public void The_same_icon_url_leaves_the_provider_unchanged()
    {
        var stored = Stored("netflix", ProviderOrigin.Catalog) with { IconUrl = "https://1000logos.net/n.png" };

        var plan = CatalogMerge.Plan(Snapshot(stored), Reading(Entry("netflix") with { IconUrl = "https://1000logos.net/n.png" }), Commit);

        Assert.Equal(["netflix"], plan.Report.ProvidersUnchanged);
        Assert.Empty(plan.Providers);
    }
}
