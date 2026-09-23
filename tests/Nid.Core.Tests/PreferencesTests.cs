using Nid.Core.Contracts;

namespace Nid.Core.Tests;

public class PreferencesTests
{
    [Fact]
    public void A_change_replaces_only_the_keys_it_names()
    {
        var updated = Preferences.Default.Apply(new PreferencesChange("sauge", null, null)).Ok();

        Assert.Equal(new Preferences("sauge", "system", false), updated);
    }

    [Fact]
    public void An_empty_change_keeps_everything() =>
        Assert.Equal(Preferences.Default, Preferences.Default.Apply(new PreferencesChange(null, null, null)).Ok());

    [Theory]
    [InlineData("Sauge")]
    [InlineData("1theme")]
    [InlineData("")]
    [InlineData("a-theme-id-that-is-far-too-long-xyz")]
    [InlineData("sauge; color: red")]
    public void A_theme_is_a_short_lowercase_identifier(string theme)
    {
        var failure = Preferences.Default.Apply(new PreferencesChange(theme, null, null)).Fail();

        Assert.Equal("core.preference_invalid", failure.Code);
    }

    [Fact]
    public void An_unknown_theme_id_is_accepted_the_list_belongs_to_the_frontend()
        => Assert.Equal("ocean", Preferences.Default.Apply(new PreferencesChange("ocean", null, null)).Ok().Theme);

    [Theory]
    [InlineData("system")]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_three_modes_are_accepted(string mode)
        => Assert.Equal(mode, Preferences.Default.Apply(new PreferencesChange(null, mode, null)).Ok().Mode);

    [Fact]
    public void Any_other_mode_is_refused()
        => Assert.Equal("core.preference_invalid", Preferences.Default.Apply(new PreferencesChange(null, "auto", null)).Fail().Code);

    [Fact]
    public void Stored_values_that_break_the_rules_fall_back_to_the_defaults()
        => Assert.Equal(Preferences.Default, Preferences.FromStored("NOT VALID", "sepia", null));
}
