using Nid.Platform.Notifications;

namespace Nid.Platform.Tests;

public class WebhookUrlTests
{
    [Theory]
    [InlineData("https://discord.com/api/webhooks/123/abc_DEF-9")]
    [InlineData("https://discordapp.com/api/webhooks/1/x")]
    public void Discord_addresses_are_accepted(string url) => Assert.True(WebhookUrls.IsDiscord(url));

    [Theory]
    [InlineData("http://discord.com/api/webhooks/1/x")]
    [InlineData("https://discord.com.evil.test/api/webhooks/1/x")]
    [InlineData("https://discord.com/api/webhooks/abc/x")]
    [InlineData("https://user@discord.com/api/webhooks/1/x")]
    [InlineData("https://discord.com/api/webhooks/1/x\n")]
    [InlineData("https://discord.com/api/webhooks/\u0661\u0662/x")]
    [InlineData("")]
    public void Anything_else_is_refused(string url) => Assert.False(WebhookUrls.IsDiscord(url));
}
