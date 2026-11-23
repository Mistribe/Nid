using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nid.Ai.Application;
using Nid.Ai.Protocol.Contracts;
using Nid.Core.Contracts;
using Nid.Host.Mcp;
using Nid.Platform.Contracts;

namespace Nid.Host.Assistant;

/// <summary>
/// #499: the assistant's tools, called in-process through the MCP tools themselves (their argument binding, their Synapse
/// pipeline), as the person's assistant actor in a scope of its own, and audited like an MCP call with <c>via: assistant</c>.
/// </summary>
public sealed class AssistantToolInvoker : IAssistantTools, IAsyncDisposable
{
    private readonly IServiceScopeFactory scopes;
    private readonly Dictionary<string, McpServerTool> tools;

    // Never run: the SDK's tools want a server in their request context, and this one has no client to talk to.
    private readonly Lazy<McpServer> server;

    public AssistantToolInvoker(IServiceProvider root, IServiceScopeFactory scopes)
    {
        this.scopes = scopes;
        tools = root.GetServices<McpServerTool>().Where(t => AssistantCatalogue.Allowed.ContainsKey(t.ProtocolTool.Name))
            .ToDictionary(t => t.ProtocolTool.Name);
        server = new(() => McpServer.Create(new NoTransport(), root.GetRequiredService<IOptions<McpServerOptions>>().Value,
            root.GetRequiredService<ILoggerFactory>(), root));
        Offered = [.. tools.Values.OrderBy(t => t.ProtocolTool.Name, StringComparer.Ordinal)
            .Select(t => new AssistantTool(Schema(t.ProtocolTool), AssistantCatalogue.Allowed[t.ProtocolTool.Name]))];
    }

    public IReadOnlyList<AssistantTool> Offered { get; }

    public async Task<AssistantToolResult> CallAsync(PersonId person, bool sharedOnly, string tool, JsonElement arguments, int maxLength, CancellationToken ct)
    {
        if (!tools.TryGetValue(tool, out var mcp))
            return new(true, $"UNKNOWN_TOOL: '{tool}' is not one of your tools.");
        var args = AssistantCatalogue.Arguments(arguments);

        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<IActorAccessor>().Current = Actor.Assistant(person, sharedOnly);
        // Before the tool runs, as for MCP: the tool and the ids it was given, never the question or the answer (§8.4).
        await services.GetRequiredService<ISecurityEvents>().RecordAsync(
            new SecurityEvent(SecurityEventNames.McpToolCalled, person, Ids(args), new Dictionary<string, string> { ["tool"] = tool }), ct);

        var request = new RequestContext<CallToolRequestParams>(server.Value,
            new JsonRpcRequest { Id = new RequestId(Guid.NewGuid().ToString()), Method = RequestMethods.ToolsCall },
            new CallToolRequestParams { Name = tool, Arguments = args }) { Services = services, MatchedPrimitive = mcp };
        CallToolResult result;
        try
        {
            result = await mcp.InvokeAsync(request, ct);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or FormatException)
        {
            // The model's arguments, bound by the SDK: over MCP the server turns this into a tool error too.
            return new(true, $"INVALID_ARGUMENTS: {e.Message}");
        }
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        // Refused whole, as Synapse does for MCP: a cut answer would give the model wrong totals (§8.2).
        return text.Length > maxLength
            ? new(true, $"ANSWER_TOO_LARGE: The answer is {text.Length} characters, above the {maxLength} limit: narrow the request (a filter, a period, a page).")
            : new(result.IsError == true, text);
    }

    public async ValueTask DisposeAsync()
    {
        if (server.IsValueCreated)
            await server.Value.DisposeAsync();
    }

    private static List<ItemId> Ids(Dictionary<string, JsonElement> args)
        => [.. args.Values.Where(v => v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out _))
            .Select(v => new ItemId(Guid.Parse(v.GetString()!))).Distinct()];

    private static ChatTool Schema(Tool tool)
    {
        var schema = JsonNode.Parse(tool.InputSchema.GetRawText())!.AsObject();
        if (schema["properties"] is JsonObject properties)
            foreach (var name in AssistantCatalogue.Stripped)
                properties.Remove(name);
        if (schema["required"] is JsonArray required)
            schema["required"] = new JsonArray([.. required.Where(r => !AssistantCatalogue.Stripped.Contains(r!.GetValue<string>())).Select(r => r!.DeepClone())]);
        var description = (tool.Description ?? "").Replace(WriteTools.ApplyClause, ".", StringComparison.Ordinal);
        if (!description.EndsWith(McpSetup.Untrusted, StringComparison.Ordinal))
            description += " " + McpSetup.Untrusted;
        return new ChatTool(tool.Name, description, JsonSerializer.SerializeToElement(schema));
    }

    private sealed class NoTransport : ITransport
    {
        private readonly Channel<JsonRpcMessage> messages = Channel.CreateUnbounded<JsonRpcMessage>();

        public string? SessionId => null;
        public ChannelReader<JsonRpcMessage> MessageReader => messages.Reader;
        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
