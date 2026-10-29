using System.Net.Http.Headers;
using System.Text;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Nid.TestSupport;

/// <summary>4B: the SDK's own client, and raw JSON-RPC for the `initialize` path, over the test host.</summary>
public static class McpHarness
{
    public static async Task<McpClient> ClientAsync(NidApp app, string token)
    {
        var http = app.CreateHttpClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
        }, http);
        return await McpClient.CreateAsync(transport);
    }

    /// <summary>One JSON-RPC POST, as a client on <paramref name="protocolVersion"/> (null: no header) would send it.</summary>
    public static async Task<HttpResponseMessage> RawAsync(NidApp app, string? token, string json, string? protocolVersion)
    {
        using var http = app.CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (protocolVersion is not null) request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocolVersion);
        return await http.SendAsync(request);
    }

    public static string TextOf(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
}
