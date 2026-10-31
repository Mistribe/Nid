namespace Nid.Host.Web;

/// <summary>
/// <c>nid healthcheck</c>: the container's HEALTHCHECK. The chiseled image has no shell, curl or wget, so the host asks
/// itself: exit 0 when readiness answers 200, else 1. It runs before any host is built, so it is cheap.
/// </summary>
public static class HealthcheckCommand
{
    private const int DefaultPort = 8080;

    /// <summary>Readiness on loopback, on the first port of <c>ASPNETCORE_HTTP_PORTS</c> (a <c>;</c> list), else 8080.</summary>
    public static Uri DefaultUrl(string? httpPorts)
    {
        var first = httpPorts?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var port = int.TryParse(first, out var parsed) ? parsed : DefaultPort;
        return new Uri($"http://127.0.0.1:{port}/health/ready");
    }

    public static async Task<int> RunAsync(Uri url, TimeSpan timeout)
    {
        try
        {
            using var client = new HttpClient { Timeout = timeout };
            using var response = await client.GetAsync(url);
            return response.StatusCode == System.Net.HttpStatusCode.OK ? 0 : 1;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
