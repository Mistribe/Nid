using System.Text;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

public sealed class IrlTests
{
    public static byte[] Vector() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vectors", "irl", "insee-001515333.xml"));

    [Fact]
    public void Reads_the_recorded_INSEE_series()
    {
        var series = IrlReader.Read(Vector());
        Assert.Equal(12, series.Count);
        Assert.Contains((new IrlQuarter(2026, 2), 148.37m, (LocalDate?)new LocalDate(2026, 7, 12)), series);
        Assert.Equal(142.06m, series.Single(o => o.Quarter == new IrlQuarter(2023, 4)).Value);
        Assert.Equal("2026-Q2", new IrlQuarter(2026, 2).ToString());
        Assert.Equal(new IrlQuarter(2026, 2), IrlQuarter.Parse("2026-Q2"));
    }

    [Fact]
    public void A_malformed_document_throws_FormatException()
    {
        Assert.Throws<FormatException>(() => IrlReader.Read(Encoding.UTF8.GetBytes("<not-xml")));
        Assert.Throws<FormatException>(() => IrlReader.Read(Encoding.UTF8.GetBytes("<Obs TIME_PERIOD=\"2026-Q2\" OBS_VALUE=\"abc\"/>")));
        Assert.Throws<FormatException>(() => IrlReader.Read(Encoding.UTF8.GetBytes("<html>maintenance</html>"))); // no observation
    }

    [Fact]
    public void Plausible_within_minus5_plus10()
    {
        Assert.True(IrlPlausibility.Plausible(146.6m, 145.47m));
        Assert.False(IrlPlausibility.Plausible(160m, 145m));
        Assert.False(IrlPlausibility.Plausible(137m, 145m));
        Assert.True(IrlPlausibility.Plausible(146.6m, null));
    }
}
