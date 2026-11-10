using Nid.Platform.Contracts;

namespace Nid.Platform.Tests;

public class ListPageTests
{
    [Theory]
    [InlineData(null, 1, 0)]
    [InlineData(-5, 1, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(2, 2, 50)]
    [InlineData(int.MaxValue, int.MaxValue / 50, (int.MaxValue / 50 - 1) * 50)]
    public void The_page_is_clamped_and_the_skip_never_overflows(int? page, int expectedPage, int expectedSkip)
        => Assert.Equal((expectedPage, expectedSkip), ListPage.Of(page, 50));
}
