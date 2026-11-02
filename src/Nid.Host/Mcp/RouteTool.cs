using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Nid.Host.Mcp;

/// <summary>
/// Spec 4D3: the MCP tool of one <see cref="RouteAction"/>. Its arguments are the route's (`id`, `flowId`, `version`), the
/// route's JSON `body` (schema from the body type) and, as for the document tools, `sourceLabel` and `apply`.
/// </summary>
public sealed class RouteTool(RouteAction action) : McpServerTool
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public override Tool ProtocolTool { get; } = new()
    {
        Name = action.Tool,
        Description = action.Description + WriteTools.Rules,
        InputSchema = Schema(action),
        Annotations = new ToolAnnotations
        {
            ReadOnlyHint = false, DestructiveHint = action.Destructive, IdempotentHint = true, OpenWorldHint = false,
        },
    };

    public override IReadOnlyList<object> Metadata => [];

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var services = request.Services ?? throw new InvalidOperationException("The call has no services.");
        var arguments = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        var payload = new JsonObject();
        string? sourceLabel = null;
        var apply = false;
        foreach (var (name, value) in arguments)
        {
            if (name == "sourceLabel")
                sourceLabel = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            else if (name == "apply")
                apply = value.ValueKind == JsonValueKind.True;
            else
                payload[name] = JsonNode.Parse(value.GetRawText());
        }
        Guid? target = null;
        if (action.Targeted)
        {
            if (!arguments.TryGetValue("id", out var id) || id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out var item))
                return McpCalls.Error("Refused: request.invalid. payload.id: A UUID is required.");
            target = item;
        }
        return await McpCalls.ProposeAsync(services, action.Kind, target, McpCalls.Element(payload), sourceLabel, apply, null, cancellationToken);
    }

    /// <summary>The exporter requires every constructor parameter, but a client can then neither omit a nullable object (an amount a document does not print) nor always send it null.</summary>
    private static JsonNode OptionalObjects(JsonNode schema)
    {
        if (schema is not JsonObject node)
            return schema;
        if (node["properties"] is JsonObject properties)
        {
            foreach (var (_, property) in properties)
                OptionalObjects(property!);
            if (node["required"] is JsonArray required)
                node["required"] = new JsonArray([.. required.Select(r => r!.GetValue<string>())
                    .Where(name => !IsNullableObject(properties[name])).Select(name => (JsonNode)name)]);
        }
        if (node["items"] is { } items)
            OptionalObjects(items);
        return node;
    }

    private static bool IsNullableObject(JsonNode? property)
        => property?["type"] is JsonArray types && types.Any(t => t!.GetValue<string>() == "object") && types.Any(t => t!.GetValue<string>() == "null");

    private static JsonElement Schema(RouteAction action)
    {
        var properties = new JsonObject();
        foreach (var name in action.RouteParameters)
            properties[name] = action.IsInteger(name)
                ? new JsonObject { ["type"] = "integer", ["description"] = $"The {name}." }
                : action.IsDate(name) ? new JsonObject { ["type"] = "string", ["format"] = "date", ["description"] = $"The {name} date (yyyy-mm-dd)." }
                : action.IsMonth(name) ? new JsonObject { ["type"] = "string", ["pattern"] = "^[0-9]{4}-(0[1-9]|1[0-2])$", ["description"] = "The month (yyyy-MM)." }
                : new JsonObject
                {
                    ["type"] = "string", ["format"] = "uuid",
                    ["description"] = name == "id" ? action.Targeted ? "The item's id." : "The id the tool's description names." : $"The line's id ({name}), from the item's get_* tool.",
                };
        foreach (var name in action.Query ?? [])
            properties[name] = new JsonObject { ["type"] = "integer", ["description"] = "The line's version, from the item's get_* tool." };
        if (action.Body is { } body)
            properties["body"] = OptionalObjects(JsonSchemaExporter.GetJsonSchemaAsNode(Json, body, new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true }));
        properties["sourceLabel"] = new JsonObject
        {
            ["type"] = "string", ["description"] = "The document's name, if the proposal comes from one, 200 characters at most.",
        };
        properties["apply"] = new JsonObject { ["type"] = "boolean", ["description"] = "true: write directly instead of proposing." };
        JsonArray required = [.. action.RouteParameters.Concat(action.Query ?? []).Concat(action.Body is null ? [] : ["body"]).Select(r => (JsonNode)r)];
        return McpCalls.Element(new JsonObject
        {
            ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false,
        });
    }
}
