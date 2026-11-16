using Nid.Core.Application.Calendar;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Core.Tests;

/// <summary>Issue #243: the modules a calendar feed lists.</summary>
public class CalendarFeedOptionsTests
{
    [Fact]
    public void No_modules_or_all_of_them_is_stored_as_every_module()
    {
        Assert.Null(CalendarFeeds.Options("me", false).Ok().Modules);
        Assert.Null(CalendarFeeds.Options("me", false, [.. CalendarModules.All.Reverse()]).Ok().Modules);
    }

    [Fact]
    public void Chosen_modules_are_kept_once_in_display_order()
        => Assert.Equal([CalendarModules.Loans, CalendarModules.Tax],
            CalendarFeeds.Options("household", true, [CalendarModules.Tax, CalendarModules.Loans, CalendarModules.Tax]).Ok().Modules);

    [Theory]
    [InlineData("")]
    [InlineData("tax,banking")]
    public void No_module_or_an_unknown_one_is_refused_on_modules(string modules)
    {
        var refused = CalendarFeeds.Options("me", false, modules.Split(',', StringSplitOptions.RemoveEmptyEntries)).Fail();
        Assert.Equal(TokenCodes.CalendarModulesInvalid, refused.Code);
        Assert.Equal("modules", Assert.Single(FieldErrors.Of(refused)!).Path);
    }
}
