using Nid.Subscriptions.Domain.Catalog;

namespace Nid.Subscriptions.Tests.Catalog;

/// <summary>Review Focus 4: whatever the name, the generated key is a key.</summary>
public class ProviderSlugTests
{
    [Theory]
    [InlineData("Tinder", "tinder")]
    [InlineData("Crème brûlée", "creme-brulee")]
    [InlineData("Disney+", "disney")]
    [InlineData("  Canal+ Séries  ", "canal-series")]
    [InlineData("Ünïcödé 2", "unicode-2")]
    [InlineData("A -- B", "a-b")]
    [InlineData("🎬🍿", "provider")]
    [InlineData("---", "provider")]
    [InlineData("ß", "provider")]
    public void A_name_gives_its_slug(string name, string slug) => Assert.Equal(slug, ProviderRules.Slug(name));

    [Fact]
    public void A_long_name_is_cut_so_the_key_stays_within_64_characters_and_never_ends_on_a_hyphen()
    {
        Assert.Equal(new string('a', 57), ProviderRules.Slug(new string('a', 100)));
        Assert.Equal(new string('a', 56), ProviderRules.Slug(new string('a', 56) + " " + new string('b', 43)));
    }

    [Fact]
    public void Every_slug_with_its_suffix_is_a_key()
    {
        foreach (var name in new[] { "Tinder", "🎬🍿", "Crème brûlée", new string('é', 100), "x", "9 lives" })
            Assert.True(ProviderRules.IsKey($"{ProviderRules.Slug(name)}-k3f9qa"), name);
    }
}
