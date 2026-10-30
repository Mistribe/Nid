namespace Nid.Platform.IntegrationTests;

/// <summary>A Discord webhook token lives in the URL path, which the HTTP instrumentation records in url.full.</summary>
public class TraceFilterTests
{
    [Theory]
    [InlineData("https://discord.com/api/webhooks/123/secretToken", false)]
    [InlineData("https://discord.com/api/webhooks/123/secretToken?wait=true", false)]
    [InlineData("https://api.enablebanking.com/sessions/0b9f6c2e-3f64-4d3a-8a51-6d1a5c7b9e20", false)]
    [InlineData("https://api.enablebanking.com/accounts/3e0b5f58/balances", false)]
    [InlineData("https://api.example.test/v1/prices", true)]
    public void Webhook_calls_are_not_traced(string url, bool traced) =>
        Assert.Equal(traced, Microsoft.Extensions.Hosting.Extensions.IsTraceable(new HttpRequestMessage(HttpMethod.Post, url)));
}
