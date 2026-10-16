using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Mcp;

/// <summary>
/// Spec 4B §2: the one way a tool runs a query. Same pipeline as REST (actor set by the call filter), same JSON as REST
/// (the host's HTTP JSON options), a refusal as a short tool error, a result above the limit refused whole.
/// </summary>
public static class McpCalls
{
    public const string TooLarge = "Result too large: narrow the request.";
    public const string NotFound = "Not found.";

    public static Task<CallToolResult> RunAsync<T>(IServiceProvider services, Func<IQuery<T>> build, CancellationToken ct)
        where T : notnull
        => RunAsync<T, T>(services, build, view => view, ct);

    public static async Task<CallToolResult> RunAsync<T, TOut>(
        IServiceProvider services, Func<IQuery<T>> build, Func<T, TOut> map, CancellationToken ct)
        where T : notnull
    {
        IQuery<T> request;
        try
        {
            request = build();
        }
        catch (BadHttpRequestException unreadable)
        {
            return Error(unreadable.Message);
        }

        var result = await services.GetRequiredService<IInvoker>().InvokeAsync(request, ct);
        if (result.TryGetFailure(out var failure))
            return Error(failure is NotFoundFailure or UnauthorizedFailure ? NotFound : $"Refused: {failure!.Code}.");

        result.TryGetValue(out var view);
        var json = JsonSerializer.Serialize(map(view!), services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions);
        var limit = services.GetRequiredService<IConfiguration>().GetValue("Mcp:MaxResultBytes", 102_400);
        return System.Text.Encoding.UTF8.GetByteCount(json) > limit
            ? Error(TooLarge)
            : new CallToolResult { Content = [new TextContentBlock { Text = json }] };
    }

    public static CallToolResult Error(string message)
        => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
