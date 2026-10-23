using System.Text.Json;
using System.Text.Json.Nodes;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Host.Mcp;

/// <summary>
/// Spec 4D3: the proposal kind of one <see cref="RouteAction"/>. Its payload is the route's arguments
/// (`{ id, flowId, version, body }`); validating it runs the route without committing, approving it runs the route. A
/// route without an `{id}` creates its item (or names its items in the body): the proposal has no target.
/// </summary>
public sealed class RouteProposal(RouteAction action, RouteReplay replay, IServiceProvider services) : IProposalHandler
{
    public string Module => action.Kind.Split('.')[0];
    public string Kind => action.Kind;
    public int PayloadVersion => 1;
    public bool NeedsTarget => action.Targeted;

    public async Task<Result<ProposalCheck>> ValidateAsync(ItemId target, JsonElement payload, bool edited, CancellationToken ct)
    {
        var read = Read(action, payload);
        if (!read.TryGetValue(out var call))
            return Result.Failure<ProposalCheck>(read.TryGetFailure(out var unreadable) ? unreadable! : throw new InvalidOperationException());
        if (action.Targeted && call!.Route["id"] != target.Value.ToString())
            return Result.Failure<ProposalCheck>(FieldErrors.Shape([new FieldError("payload.id", FieldErrors.ShapeCode, "The proposal's target item.")]));
        var dry = await replay.DryRunAsync(action, call!, ct);
        return dry.TryGetFailure(out var refused)
            ? Result.Failure<ProposalCheck>(refused!)
            : Result.Success(new ProposalCheck(null, [], Normalized(call!)));
    }

    /// <summary>The generic screen shows the payload itself: no current state to compare.</summary>
    public Task<JsonElement> PreviewAsync(ItemId target, JsonElement payload, CancellationToken ct)
        => Task.FromResult(JsonSerializer.SerializeToElement(new { }));

    public async Task<Result<IReadOnlyList<Guid>>> ApplyAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        var read = Read(action, payload);
        if (!read.TryGetValue(out var call))
            return Result.Failure<IReadOnlyList<Guid>>(read.TryGetFailure(out var unreadable) ? unreadable! : throw new InvalidOperationException());
        var ran = await replay.RunAsync(services, action, call!, ct);
        if (!ran.TryGetValue(out var answer))
            return Result.Failure<IReadOnlyList<Guid>>(ran.TryGetFailure(out var refused) ? refused! : throw new InvalidOperationException());
        // A creation answers its id (`IdResponse`, a person or a provider): what the proposal applied.
        return Result.Success<IReadOnlyList<Guid>>(answer is { ValueKind: JsonValueKind.Object } json
            && json.TryGetProperty("id", out var id) && id.TryGetGuid(out var created) ? [created] : []);
    }

    /// <summary>The payload's fields: the route's parameters (UUIDs), its query values and its body (an object), nothing else.</summary>
    public static Result<RouteCall> Read(RouteAction action, JsonElement payload)
    {
        var errors = new List<FieldError>();
        var route = new Dictionary<string, string>();
        var query = new Dictionary<string, string>();
        JsonElement? body = null;
        foreach (var field in payload.EnumerateObject())
        {
            if (action.RouteParameters.Contains(field.Name))
            {
                if (action.IsInteger(field.Name) && field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out var n))
                    route[field.Name] = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                else if (action.IsDate(field.Name) && field.Value.ValueKind == JsonValueKind.String && LocalDatePattern.Iso.Parse(field.Value.GetString()!).Success)
                    route[field.Name] = field.Value.GetString()!;
                else if (!action.IsInteger(field.Name) && !action.IsDate(field.Name) && field.Value.ValueKind == JsonValueKind.String
                         && Guid.TryParse(field.Value.GetString(), out var id))
                    route[field.Name] = id.ToString();
                else
                    errors.Add(new FieldError($"payload.{field.Name}", FieldErrors.ShapeCode, Required(action, field.Name)));
            }
            else if (action.Query?.Contains(field.Name) == true)
            {
                if (field.Value.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                    query[field.Name] = field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString()! : field.Value.GetRawText();
                else if (field.Value.ValueKind != JsonValueKind.Null)
                    errors.Add(new FieldError($"payload.{field.Name}", FieldErrors.ShapeCode, "A number is required."));
            }
            else if (field.Name == "body" && action.Body is not null)
            {
                if (field.Value.ValueKind == JsonValueKind.Object)
                    body = field.Value.Clone();
                else
                    errors.Add(new FieldError("payload.body", FieldErrors.ShapeCode, "A JSON object is required."));
            }
            else
                errors.Add(new FieldError($"payload.{field.Name}", FieldErrors.ShapeCode, "Unknown field."));
        }
        errors.AddRange(action.RouteParameters.Where(p => !route.ContainsKey(p) && errors.All(e => e.Path != $"payload.{p}"))
            .Select(p => new FieldError($"payload.{p}", FieldErrors.ShapeCode, Required(action, p))));
        if (action.Body is not null && body is null && errors.All(e => e.Path != "payload.body"))
            errors.Add(new FieldError("payload.body", FieldErrors.ShapeCode, "A JSON object is required."));
        return errors.Count > 0 ? Result.Failure<RouteCall>(FieldErrors.Shape(errors)) : Result.Success(new RouteCall(route, query, body));
    }

    private static string Required(RouteAction action, string parameter)
        => action.IsInteger(parameter) ? "An integer is required." : action.IsDate(parameter) ? "An ISO date (yyyy-mm-dd) is required." : "A UUID is required.";

    private static JsonElement Normalized(RouteCall call)
    {
        var json = new JsonObject();
        foreach (var (name, value) in call.Route)
            json[name] = int.TryParse(value, out var year) ? year : value; // a `{year:int}`: a UUID never parses
        foreach (var (name, value) in call.Query)
            json[name] = long.TryParse(value, out var number) ? number : value;
        if (call.Body is { } body)
            json["body"] = JsonNode.Parse(body.GetRawText());
        return JsonSerializer.SerializeToElement(json);
    }
}
