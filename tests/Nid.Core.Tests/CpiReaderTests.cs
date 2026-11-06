using System.Text;
using Nid.Core.Domain.Inflation;
using NodaTime;

namespace Nid.Core.Tests;

public sealed class CpiReaderTests
{
    static byte[] Vector() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vectors", "cpi", "insee-011814630.xml"));
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Read_keeps_definitive_months_only()
    {
        var read = CpiReader.Read(Vector());
        Assert.Equal(new YearMonth(1996, 1), read.Min(o => o.Month));
        Assert.Equal(new YearMonth(2026, 9), read.Max(o => o.Month)); // 2026-10 is provisional
        Assert.Equal((103.07m, new LocalDate(2026, 10, 16)),
            read.Where(o => o.Month == new YearMonth(2026, 9)).Select(o => (o.Value, o.PublishedOn!.Value)).Single());
    }

    [Fact]
    public void Read_throws_when_no_definitive_observation()
    {
        Assert.Throws<FormatException>(() => CpiReader.Read(Utf8("<S><Obs TIME_PERIOD=\"2026-10\" OBS_VALUE=\"103.47\" OBS_QUAL=\"P\"/></S>")));
        Assert.Throws<FormatException>(() => CpiReader.Read(Utf8("<html>maintenance</html>")));
    }

    [Fact]
    public void Read_throws_on_a_malformed_document()
    {
        Assert.Throws<FormatException>(() => CpiReader.Read(Utf8("<not-xml")));
        Assert.Throws<FormatException>(() => CpiReader.Read(Utf8("<Obs TIME_PERIOD=\"2026-Q3\" OBS_VALUE=\"103\" OBS_QUAL=\"DEF\"/>")));
        Assert.Throws<FormatException>(() => CpiReader.Read(Utf8("<Obs TIME_PERIOD=\"2026-09\" OBS_VALUE=\"abc\" OBS_QUAL=\"DEF\"/>")));
    }

    [Fact]
    public void Read_skips_an_Obs_without_OBS_QUAL()
        => Assert.Throws<FormatException>(() => CpiReader.Read(Utf8("<S><Obs TIME_PERIOD=\"2026-09\" OBS_VALUE=\"103.07\"/></S>")));

    [Fact]
    public void Read_throws_when_a_definitive_Obs_lacks_a_value_or_a_month()
    {
        Assert.Throws<FormatException>(() => CpiReader.Read(Utf8("<Obs TIME_PERIOD=\"2026-09\" OBS_QUAL=\"DEF\"/>")));
        Assert.Throws<FormatException>(() => CpiReader.Read(Utf8("<Obs OBS_VALUE=\"103.07\" OBS_QUAL=\"DEF\"/>")));
    }

    [Theory]
    [InlineData(100, 103, true)]
    [InlineData(100, 103.01, false)]
    [InlineData(100, 97, true)]
    [InlineData(100, 96.99, false)]
    public void Plausible_within_three_percent_of_the_previous_month(decimal previous, decimal value, bool plausible)
        => Assert.Equal(plausible, CpiPlausibility.Plausible(value, previous));

    [Fact]
    public void Plausible_without_a_previous_month() => Assert.True(CpiPlausibility.Plausible(50m, null));
}
