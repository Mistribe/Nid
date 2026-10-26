namespace Nid.Subscriptions.IntegrationTests.Notifications;

/// <summary>A Discord webhook token lives in the URL path, which the HTTP instrumentation records in url.full.</summary>
public class TraceFilterTests
{
    [Theory]
    [InlineData("https://discord.com/api/webhooks/123/secretToken", false)]
    [InlineData("https://discord.com/api/webhooks/123/secretToken?wait=true", false)]
    [InlineData("https://api.example.test/v1/prices", true)]
    public void Webhook_calls_are_not_traced(string url, bool traced) =>
        Assert.Equal(traced, Microsoft.Extensions.Hosting.Extensions.IsTraceable(new HttpRequestMessage(HttpMethod.Post, url)));
}
