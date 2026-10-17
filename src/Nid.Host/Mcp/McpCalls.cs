using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Mcp;

/// <summary>
/// Spec 4B §2: the one way a tool runs a query. Same pipeline as REST (actor set by the call filter), same JSON as REST
/// (the host's HTTP JSON options), a refusal as a short tool error, a result above the limit refused whole. Spec 4D1 §5:
/// and the one way a write tool proposes or writes a document.
/// </summary>
public static class McpCalls
{
    public const string TooLarge = "Result too large: narrow the request.";
    public const string NotFound = "Not found.";

    /// <summary>A tool's arguments as a proposal payload: camelCase, absent fields left out (the handler normalizes the rest).</summary>
    private static readonly JsonSerializerOptions Arguments = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

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
        return Answer(services, map(view!));
    }

    /// <summary>
    /// Spec 4D1 §5: a document proposed (the default) or written directly (`apply`). The scope is checked first, so a
    /// missing one is named rather than answered « Not found. »; a refusal names its code and each refused field.
    /// </summary>
    public static async Task<CallToolResult> ProposeAsync(
        IServiceProvider services, string kind, Guid target, object payload, string? sourceLabel, bool apply, CancellationToken ct)
    {
        if (services.GetRequiredService<IActorAccessor>().Current?.Has(apply ? ScopeNames.WriteDirect : ScopeNames.Write) != true)
            return Error(apply
                ? $"Refused: applying directly needs the '{ScopeNames.WriteDirect}' scope; call again without apply to propose."
                : $"Refused: proposing needs the '{ScopeNames.Write}' scope.");

        var body = JsonSerializer.SerializeToElement(payload, Arguments);
        var invoker = services.GetRequiredService<IInvoker>();
        if (apply)
        {
            var written = await invoker.InvokeAsync(new ApplyDirectCommand(kind, new ItemId(target), body), ct);
            if (!written.TryGetValue(out var write))
                return Refusal(written.TryGetFailure(out var refused) ? refused! : throw new InvalidOperationException());
            return write!.Conforme is { } same
                ? Answer(services, Conforme(same), limited: false)
                : Answer(services, new { outcome = "applied", appliedRefs = write.AppliedRefs, skipped = write.Skipped }, limited: false);
        }

        var submitted = await invoker.InvokeAsync(new SubmitProposalCommand(kind, new ItemId(target), body, sourceLabel), ct);
        if (!submitted.TryGetValue(out var outcome))
            return Refusal(submitted.TryGetFailure(out var rejected) ? rejected! : throw new InvalidOperationException());
        return outcome!.Proposal is null
            ? Answer(services, Conforme(outcome.Conforme!.Value), limited: false)
            : Answer(services, new { outcome = "proposed", created = outcome.Created, proposal = outcome.Proposal }, limited: false);
    }

    private static object Conforme(JsonElement same)
        => new { outcome = "conforme", expected = same.GetProperty("expected"), given = same.GetProperty("given") };

    /// <summary>Not found and unauthorized stay « Not found. »; any other refusal names its code, then each field (`payload.movements[3].amount: …`) or its message.</summary>
    private static CallToolResult Refusal(Failure failure)
    {
        if (failure is NotFoundFailure or UnauthorizedFailure)
            return Error(NotFound);
        var detail = FieldErrors.Of(failure) is { Count: > 0 } fields
            ? string.Join("; ", fields.Select(f => $"{f.Path}: {f.Message}"))
            : failure.Metadata is { } metadata && metadata.TryGetValue("field", out var field) && field is string path
                ? $"{path}: {failure.Message}"
                : failure.Message;
        return Error($"Refused: {failure.Code}. {detail}");
    }

    /// <summary>A read's result above <c>Mcp:MaxResultBytes</c> is refused whole; a write's answer (<paramref name="limited"/> false) only echoes its input, bounded per field, and is never refused after the write.</summary>
    private static CallToolResult Answer<TOut>(IServiceProvider services, TOut value, bool limited = true)
    {
        var json = JsonSerializer.Serialize(value, services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions);
        var limit = services.GetRequiredService<IConfiguration>().GetValue("Mcp:MaxResultBytes", 102_400);
        return limited && System.Text.Encoding.UTF8.GetByteCount(json) > limit
            ? Error(TooLarge)
            : new CallToolResult { Content = [new TextContentBlock { Text = json }] };
    }

    public static CallToolResult Error(string message)
        => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
