using Nid.Core.Contracts;

namespace Nid.Core.Tests;

public class PreferencesTests
{
    [Fact]
    public void A_change_replaces_only_the_keys_it_names()
    {
        var updated = Preferences.Default.Apply(new PreferencesChange("sauge", null, null, null)).Ok();

        Assert.Equal(new Preferences("sauge", "system", false, Preferences.NoGuides), updated);
    }

    [Fact]
    public void An_empty_change_keeps_everything() =>
        Assert.Equal(Preferences.Default, Preferences.Default.Apply(new PreferencesChange(null, null, null, null)).Ok());

    [Theory]
    [InlineData("Sauge")]
    [InlineData("1theme")]
    [InlineData("")]
    [InlineData("a-theme-id-that-is-far-too-long-xyz")]
    [InlineData("sauge; color: red")]
    [InlineData("sauge\n")]
    public void A_theme_is_a_short_lowercase_identifier(string theme)
    {
        var failure = Preferences.Default.Apply(new PreferencesChange(theme, null, null, null)).Fail();

        Assert.Equal("core.preference_invalid", failure.Code);
    }

    [Fact]
    public void An_unknown_theme_id_is_accepted_the_list_belongs_to_the_frontend()
        => Assert.Equal("ocean", Preferences.Default.Apply(new PreferencesChange("ocean", null, null, null)).Ok().Theme);

    [Theory]
    [InlineData("system")]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_three_modes_are_accepted(string mode)
        => Assert.Equal(mode, Preferences.Default.Apply(new PreferencesChange(null, mode, null, null)).Ok().Mode);

    [Fact]
    public void Any_other_mode_is_refused()
        => Assert.Equal("core.preference_invalid", Preferences.Default.Apply(new PreferencesChange(null, "auto", null, null)).Fail().Code);

    [Fact]
    public void Stored_values_that_break_the_rules_fall_back_to_the_defaults()
        => Assert.Equal(Preferences.Default, Preferences.FromStored("NOT VALID", "sepia", null, null));

    [Fact]
    public void A_guides_map_replaces_the_whole_map()
    {
        var first = Preferences.Default.Apply(new PreferencesChange(null, null, null, new Dictionary<string, int> { ["dashboard"] = 1 })).Ok();
        var second = first.Apply(new PreferencesChange(null, null, null, new Dictionary<string, int> { ["loans"] = 2 })).Ok();
        Assert.Equal(new Dictionary<string, int> { ["loans"] = 2 }, second.GuidesSeen);
    }

    [Fact]
    public void An_empty_guides_map_resets_them()
        => Assert.Empty(Preferences.Default.Apply(new PreferencesChange(null, null, null, new Dictionary<string, int> { ["a"] = 1 })).Ok()
            .Apply(new PreferencesChange(null, null, null, new Dictionary<string, int>())).Ok().GuidesSeen);

    [Fact]
    public void A_change_without_guides_keeps_them() // the map survives a theme change
        => Assert.Equal(1, Preferences.Default.Apply(new PreferencesChange(null, null, null, new Dictionary<string, int> { ["a"] = 1 })).Ok()
            .Apply(new PreferencesChange("sauge", null, null, null)).Ok().GuidesSeen["a"]);

    [Theory]
    [InlineData("Dashboard", 1)]
    [InlineData("1loans", 1)]
    [InlineData("a-guide-id-that-is-far-too-long-xyz", 1)]
    [InlineData("loans", 0)]
    [InlineData("loans", 1000)]
    [InlineData("loans\n", 1)]
    public void A_guide_is_a_short_lowercase_id_with_a_version_from_1_to_999(string id, int version)
        => Assert.Equal("core.preference_invalid",
            Preferences.Default.Apply(new PreferencesChange(null, null, null, new Dictionary<string, int> { [id] = version })).Fail().Code);

    [Fact]
    public void At_most_32_guides()
        => Assert.Equal("core.preference_invalid", Preferences.Default.Apply(new PreferencesChange(null, null, null,
            Enumerable.Range(0, 33).ToDictionary(i => $"g{i}", _ => 1))).Fail().Code);

    [Fact]
    public void A_stored_entry_that_breaks_the_rules_is_dropped_alone()
        => Assert.Equal(new Dictionary<string, int> { ["loans"] = 2 },
            Preferences.FromStored(null, null, null, new Dictionary<string, int> { ["loans"] = 2, ["BAD"] = 1, ["x"] = 0 }).GuidesSeen);
}
